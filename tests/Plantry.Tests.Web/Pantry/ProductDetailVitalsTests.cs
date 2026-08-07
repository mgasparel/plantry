using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Pantry.Domain;
using Plantry.Pantry.Application;
using Plantry.Identity.Application;
using Plantry.Market.Application;
using Plantry.Market.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;
using CatalogUnit = Plantry.Pantry.Domain.Unit;

namespace Plantry.Tests.Web.Pantry;

/// <summary>
/// L4 Web integration tests for the vitals strip and the per-lot plantry-fdoq expiry hybrid
/// (plantry-sbpk) — the On hand / Next-expiry / Low-stock-alert tiles across the running-low, sparse
/// and zero-stock states the ticket's acceptance criteria name, plus the lot-row expiry pill/muted-date
/// split and the History "Change" column's delta colour lanes. Reuses the fake seams
/// <see cref="ProductDetailSetPriceTests"/> established for this page (assembly-visible internal fakes)
/// and mirrors <see cref="ProductDetailMarkOpenedTests"/>'s per-test-instance stock-seeding shape, since
/// each fact here needs a differently-shaped <see cref="ProductStock"/>.
/// </summary>
public sealed class ProductDetailVitalsTests : IDisposable
{
    private ProductDetailVitalsFactory? _factory;

    public void Dispose() => _factory?.Dispose();

    private HttpClient AuthClient(ProductStock stock)
    {
        _factory = new ProductDetailVitalsFactory(stock);
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ProductDetailVitalsFixture.HouseholdId.ToString());
        return client;
    }

    [Fact(DisplayName = "Vitals — running low colours the On-hand value and states 'Running low'")]
    public async Task Vitals_RunningLow_ColoursOnHandAndStatesRunningLow()
    {
        var stock = ProductStock.Start(ProductDetailVitalsFixture.Household, ProductDetailVitalsFixture.ProductId, ProductDetailVitalsFixture.Clock);
        stock.AddStock(4m, ProductDetailVitalsFixture.UnitId, ProductDetailVitalsFixture.LocationId, Guid.NewGuid(), ProductDetailVitalsFixture.Clock);
        stock.SetLowStockThreshold(5m, ProductDetailVitalsFixture.Clock);
        var client = AuthClient(stock);

        var html = await (await client.GetAsync($"/Pantry/Products/Detail/{ProductDetailVitalsFixture.ProductId}"))
            .Content.ReadAsStringAsync();

        Assert.Contains("vital__val--warn", html, StringComparison.Ordinal);
        Assert.Contains("Running low", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Vitals — zero stock renders 'None' / 'not in stock' on the On-hand tile")]
    public async Task Vitals_ZeroStock_RendersNoneNotInStock()
    {
        var stock = ProductStock.Start(ProductDetailVitalsFixture.Household, ProductDetailVitalsFixture.ProductId, ProductDetailVitalsFixture.Clock);
        var client = AuthClient(stock);

        var html = await (await client.GetAsync($"/Pantry/Products/Detail/{ProductDetailVitalsFixture.ProductId}"))
            .Content.ReadAsStringAsync();

        Assert.Contains("id=\"product-total\"", html, StringComparison.Ordinal);
        Assert.Contains("None", html, StringComparison.Ordinal);
        Assert.Contains("not in stock", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Vitals — no threshold set renders the empty Low-stock-alert tile with its CTA")]
    public async Task Vitals_NoThreshold_RendersEmptyAlertTile()
    {
        var stock = ProductStock.Start(ProductDetailVitalsFixture.Household, ProductDetailVitalsFixture.ProductId, ProductDetailVitalsFixture.Clock);
        stock.AddStock(4m, ProductDetailVitalsFixture.UnitId, ProductDetailVitalsFixture.LocationId, Guid.NewGuid(), ProductDetailVitalsFixture.Clock);
        var client = AuthClient(stock);

        var html = await (await client.GetAsync($"/Pantry/Products/Detail/{ProductDetailVitalsFixture.ProductId}"))
            .Content.ReadAsStringAsync();

        Assert.Contains("id=\"product-low-stock-alert\"", html, StringComparison.Ordinal);
        Assert.Contains("Set alert ›", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Vitals + lot row — inside the household horizon renders a relative label and the expiry pill")]
    public async Task Vitals_LotInsideHorizon_RendersRelativeLabelAndPill()
    {
        var today = DateOnly.FromDateTime(ProductDetailVitalsFixture.Instant.UtcDateTime);
        var stock = ProductStock.Start(ProductDetailVitalsFixture.Household, ProductDetailVitalsFixture.ProductId, ProductDetailVitalsFixture.Clock);
        // 1 day out — inside the fake 7-day "expiring soon" horizon (AddFakeExpiringSoonHorizon default).
        stock.AddStock(2m, ProductDetailVitalsFixture.UnitId, ProductDetailVitalsFixture.LocationId, Guid.NewGuid(),
            ProductDetailVitalsFixture.Clock, expiryDate: today.AddDays(1));
        var client = AuthClient(stock);

        var html = await (await client.GetAsync($"/Pantry/Products/Detail/{ProductDetailVitalsFixture.ProductId}"))
            .Content.ReadAsStringAsync();

        Assert.Contains("Tomorrow", html, StringComparison.Ordinal);
        Assert.Contains("badge-expiry badge-expiry--", html, StringComparison.Ordinal);
        // The lots list MUST carry the non-clipping modifier or its row-actions-menu panel is
        // clipped by .catalog-list's own overflow:hidden (plantry-sbpk pass-3 fix).
        Assert.Contains("catalog-list catalog-list--menus", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Lot row — beyond the household horizon renders the muted absolute date, no pill")]
    public async Task Vitals_LotBeyondHorizon_RendersAbsoluteDateNoPill()
    {
        var today = DateOnly.FromDateTime(ProductDetailVitalsFixture.Instant.UtcDateTime);
        var stock = ProductStock.Start(ProductDetailVitalsFixture.Household, ProductDetailVitalsFixture.ProductId, ProductDetailVitalsFixture.Clock);
        // 30 days out — beyond the fake 7-day horizon.
        stock.AddStock(2m, ProductDetailVitalsFixture.UnitId, ProductDetailVitalsFixture.LocationId, Guid.NewGuid(),
            ProductDetailVitalsFixture.Clock, expiryDate: today.AddDays(30));
        var client = AuthClient(stock);

        var html = await (await client.GetAsync($"/Pantry/Products/Detail/{ProductDetailVitalsFixture.ProductId}"))
            .Content.ReadAsStringAsync();

        Assert.Contains("lot-row__expiry", html, StringComparison.Ordinal);
        Assert.DoesNotContain("badge-expiry", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "History — the Change column colours a positive delta 'in' and a negative delta 'out'")]
    public async Task History_DeltaColourLanes_InAndOut()
    {
        var stock = ProductStock.Start(ProductDetailVitalsFixture.Household, ProductDetailVitalsFixture.ProductId, ProductDetailVitalsFixture.Clock);
        var entry = stock.AddStock(5m, ProductDetailVitalsFixture.UnitId, ProductDetailVitalsFixture.LocationId, Guid.NewGuid(), ProductDetailVitalsFixture.Clock);
        var consumeResult = stock.Consume(
            2m, ProductDetailVitalsFixture.UnitId, StockReason.Consumed, new IdentityQuantityConverter(),
            Guid.NewGuid(), ProductDetailVitalsFixture.Clock, targetEntry: entry.Id);
        Assert.True(consumeResult.IsSuccess);
        var client = AuthClient(stock);

        var html = await (await client.GetAsync($"/Pantry/Products/Detail/{ProductDetailVitalsFixture.ProductId}"))
            .Content.ReadAsStringAsync();

        Assert.Contains("h-delta--in", html, StringComparison.Ordinal);
        Assert.Contains("h-delta--out", html, StringComparison.Ordinal);
    }
}

// ── Fixture data ──────────────────────────────────────────────────────────────

internal static class ProductDetailVitalsFixture
{
    internal static readonly Guid HouseholdId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    internal static readonly HouseholdId Household = Plantry.SharedKernel.HouseholdId.From(HouseholdId);

    /// <summary>Pinned instant (mirrors <c>MealPlanningTestClock</c>) so the WAF-hosted SUT and this
    /// fixture always agree on "today" — <see cref="FixedClock"/> resolves <c>DetailModel.TodayDate</c>
    /// off this same value rather than racing two independent reads of the real system clock, which on
    /// a machine whose local zone diverges from UTC could flip which side of midnight "today" falls on.</summary>
    internal static readonly DateTimeOffset Instant = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    internal static readonly IClock Clock = new FixedClock(Instant);

    internal static readonly Guid ProductId = Guid.Parse("aaaaaaaa-1111-0000-0000-aaa000000001");
    internal static readonly Guid UnitId = Guid.Parse("bbbbbbbb-1111-0000-0000-bbb000000001");
    internal static readonly Guid LocationId = Guid.Parse("cccccccc-1111-0000-0000-ccc000000001");

    internal static CatalogUnit BuildUnit() =>
        CatalogUnit.Create(Household, "srv", "Servings", Dimension.Count, 1m, isBase: true);
}

/// <summary>A no-op unit converter (same unit both directions) for the <see cref="ProductStock.Consume"/>
/// call this file's delta-colour test needs — the private nested identity converter in
/// <c>ProductDetailSetPriceTests.IdentityConversionProvider</c> is not accessible outside that file.</summary>
internal sealed class IdentityQuantityConverter : IQuantityConverter
{
    public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId) => amount;
}

// ── WAF factory ───────────────────────────────────────────────────────────────

internal sealed class ProductDetailVitalsFactory(ProductStock stock) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.AddFakeExpiringSoonHorizon();
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            var unit = ProductDetailVitalsFixture.BuildUnit();

            services.RemoveAll<IUnitRepository>();
            services.AddSingleton<IUnitRepository>(new FakeSingleUnitRepository(unit));

            services.RemoveAll<ICatalogReadFacade>();
            services.AddSingleton<ICatalogReadFacade>(new FakeCatalogReadFacade(ProductDetailVitalsFixture.ProductId, unit));

            var stockRepo = new FakeDetailStockRepository();
            stockRepo.Items.Add(stock);
            services.RemoveAll<IProductStockRepository>();
            services.AddSingleton<IProductStockRepository>(stockRepo);

            services.RemoveAll<IProductConversionProvider>();
            services.AddSingleton<IProductConversionProvider>(new IdentityConversionProvider());

            services.RemoveAll<IStockProvenanceReader>();
            services.AddSingleton<IStockProvenanceReader>(new FakeStockProvenanceReader());

            services.RemoveAll<IPriceObservationRepository>();
            services.AddSingleton<IPriceObservationRepository>(new FakePriceObservationRepository());

            services.RemoveAll<IDisplayCurrency>();
            services.AddSingleton<IDisplayCurrency>(new FakeDisplayCurrency());

            services.RemoveAll<IUnitPriceCalculator>();
            services.AddSingleton<IUnitPriceCalculator>(new FakeUnitPriceCalculator(0.5m));

            services.RemoveAll<Plantry.Recipes.Domain.IRecipeRepository>();
            services.AddSingleton<Plantry.Recipes.Domain.IRecipeRepository>(new FakeRecipeRepository());

            // Pin the SUT to the same fixed instant the fixture seeds against (plantry-sbpk pass-3
            // fix) — otherwise the WAF-hosted SUT's SystemClock and this file's independent
            // DateTime.UtcNow reads race across the local/UTC day boundary.
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(ProductDetailVitalsFixture.Clock);
        });
    }
}
