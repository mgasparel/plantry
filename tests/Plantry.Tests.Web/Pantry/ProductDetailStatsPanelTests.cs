using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Pantry.Domain;
using Plantry.Identity.Application;
using Plantry.Pantry.Application;
using Plantry.Market.Application;
using Plantry.Market.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;
using CatalogUnit = Plantry.Pantry.Domain.Unit;

namespace Plantry.Tests.Web.Pantry;

/// <summary>
/// L4 Web integration tests for the product-detail stats injection (plantry-fuej, stats-page-prototype.html
/// appendix "Catalog / Pantry product detail"): price sparkline/median, days-of-supply, and waste rate.
/// Reuses the fakes defined alongside <c>ProductDetailSetPriceTests</c> (same namespace/assembly) rather
/// than duplicating a second set — only the seeded <see cref="ProductStock"/> journal history differs
/// per test, so each test builds its own stock/prices and wires a fresh factory around them.
/// </summary>
public sealed class ProductDetailStatsPanelTests : IDisposable
{
    private static readonly Guid HouseholdId = Guid.Parse("aaaaaaaa-1111-0000-0000-000000000001");
    private static readonly Plantry.SharedKernel.HouseholdId Household = Plantry.SharedKernel.HouseholdId.From(HouseholdId);
    internal static readonly IClock Clock = new FixedClock(new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero));
    private static readonly Guid ProductId = Guid.Parse("bbbbbbbb-1111-0000-0000-bbb000000001");
    private static readonly Guid UnitId = Guid.Parse("cccccccc-1111-0000-0000-ccc000000001");
    private static readonly Guid UserId = Guid.Parse("dddddddd-1111-0000-0000-000000000aa1");
    private static readonly DateTimeOffset OlderObservation = new(2026, 2, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RecentObservation = new(2026, 3, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CurrentObservation = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private ProductDetailStatsPanelFactory? _factory;

    public void Dispose() => _factory?.Dispose();

    private HttpClient AuthClient(
        ProductStock stock,
        IReadOnlyList<PriceObservation> priceHistory,
        CatalogUnit? displayUnit = null,
        Guid? catalogDefaultUnitId = null,
        bool catalogProductExists = true)
    {
        _factory = new ProductDetailStatsPanelFactory(
            stock,
            priceHistory,
            displayUnit ?? Unit(),
            catalogDefaultUnitId,
            catalogProductExists);
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdId.ToString());
        return client;
    }

    private static CatalogUnit Unit() => CatalogUnit.Create(Household, "g", "Grams", Dimension.Mass, 1m, isBase: true);

    private static PriceObservation Purchase(decimal unitPrice, DateTimeOffset observedAt) =>
        Purchase(unitPrice, unitPrice, observedAt);

    private static PriceObservation Purchase(decimal price, decimal unitPrice, DateTimeOffset observedAt) =>
        PriceObservation.Record(
            Household, ProductId, null, price: price, quantity: 1m, unitId: UnitId, unitPrice: unitPrice,
            source: PriceSource.Purchase, merchantText: "Superstore", sourceRef: Guid.CreateVersion7(),
            observedAt: observedAt, userId: UserId);

    [Fact(DisplayName = "Detail GET — renders no Stats section when there is neither price history nor consumption history")]
    public async Task Get_RendersNoStatsSection_WhenNoDataAtAll()
    {
        var stock = ProductStock.Start(Household, ProductId, Clock);
        stock.AddStock(100m, UnitId, Guid.CreateVersion7(), UserId, Clock);
        var client = AuthClient(stock, []);

        var html = await (await client.GetAsync($"/Pantry/Products/Detail/{ProductId}"))
            .Content.ReadAsStringAsync();

        Assert.DoesNotContain("catalog-section__heading\">Stats<", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Detail GET — renders the sparkline and median once there are >= 2 usable price points")]
    public async Task Get_RendersSparklineAndMedian_WithEnoughPricePoints()
    {
        var stock = ProductStock.Start(Household, ProductId, Clock);
        stock.AddStock(100m, UnitId, Guid.CreateVersion7(), UserId, Clock);
        var priceHistory = new List<PriceObservation>
        {
            Purchase(3.00m, OlderObservation),
            Purchase(5.00m, RecentObservation),
        };
        var client = AuthClient(stock, priceHistory);

        var html = await (await client.GetAsync($"/Pantry/Products/Detail/{ProductId}"))
            .Content.ReadAsStringAsync();

        Assert.Contains("catalog-section__heading\">Stats<", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Price history trend\"", html, StringComparison.Ordinal);
        // Median of {3.00, 5.00} is 4.00, rendered per the configured base unit.
        Assert.Contains("You pay <b>$4.00/g</b> median", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Detail GET — converts the normalized median to the product's non-base default unit")]
    public async Task Get_RendersMedianConvertedToConfiguredDefaultUnit()
    {
        var pounds = CatalogUnit.Create(Household, "lb", "Pounds", Dimension.Mass, 453.592m);
        var stock = ProductStock.Start(Household, ProductId, Clock);
        stock.AddStock(100m, UnitId, Guid.CreateVersion7(), UserId, Clock);
        var priceHistory = new List<PriceObservation>
        {
            Purchase(6.00m, 6.00m / pounds.FactorToBase, OlderObservation),
            Purchase(6.99m, 6.99m / pounds.FactorToBase, new DateTimeOffset(2026, 2, 20, 12, 0, 0, TimeSpan.Zero)),
            Purchase(7.49m, 7.49m / pounds.FactorToBase, RecentObservation),
        };
        var client = AuthClient(stock, priceHistory, pounds);

        var html = await (await client.GetAsync($"/Pantry/Products/Detail/{ProductId}"))
            .Content.ReadAsStringAsync();

        Assert.Contains("aria-label=\"Price history trend\"", html, StringComparison.Ordinal);
        Assert.Contains("You pay <b>$6.99/lb</b> median", html, StringComparison.Ordinal);
        Assert.DoesNotContain("$0.02", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Detail GET — omits the median when the configured default unit cannot be resolved")]
    public async Task Get_OmitsMedianButKeepsSparkline_WhenDefaultUnitCannotBeResolved()
    {
        var unit = Unit();
        var stock = ProductStock.Start(Household, ProductId, Clock);
        stock.AddStock(1000m, UnitId, Guid.CreateVersion7(), UserId, Clock);
        var converter = new IdentityQuantityConverter();
        stock.Consume(90m, UnitId, StockReason.Consumed, converter, UserId, Clock);
        stock.Consume(90m, UnitId, StockReason.Consumed, converter, UserId, Clock);
        var missingUnitId = Guid.Parse("eeeeeeee-1111-0000-0000-eeeeeeee0001");
        var client = AuthClient(
            stock,
            [Purchase(3.00m, OlderObservation), Purchase(5.00m, RecentObservation)],
            unit,
            catalogDefaultUnitId: missingUnitId);

        var response = await client.GetAsync($"/Pantry/Products/Detail/{ProductId}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("aria-label=\"Price history trend\"", html, StringComparison.Ordinal);
        Assert.Contains("days of supply left", html, StringComparison.Ordinal);
        Assert.DoesNotContain(" median", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Detail GET — omits the median when the catalog product cannot be resolved")]
    public async Task Get_OmitsMedianButKeepsSparkline_WhenCatalogProductCannotBeResolved()
    {
        var stock = ProductStock.Start(Household, ProductId, Clock);
        stock.AddStock(100m, UnitId, Guid.CreateVersion7(), UserId, Clock);
        var client = AuthClient(
            stock,
            [Purchase(3.00m, OlderObservation), Purchase(5.00m, RecentObservation)],
            Unit(),
            catalogProductExists: false);

        var response = await client.GetAsync($"/Pantry/Products/Detail/{ProductId}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("aria-label=\"Price history trend\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(" median", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Detail GET — omits the sparkline with only a single usable price point, but a single point still doesn't crash the page")]
    public async Task Get_OmitsSparkline_WithOnlyOnePricePoint()
    {
        var stock = ProductStock.Start(Household, ProductId, Clock);
        stock.AddStock(100m, UnitId, Guid.CreateVersion7(), UserId, Clock);
        var client = AuthClient(stock, [Purchase(3.00m, CurrentObservation)]);

        var response = await client.GetAsync($"/Pantry/Products/Detail/{ProductId}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("aria-label=\"Price history trend\"", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Detail GET — renders days-of-supply and waste rate once there is enough consumption/discard history")]
    public async Task Get_RendersDaysOfSupplyAndWasteRate_WithEnoughConsumptionHistory()
    {
        var stock = ProductStock.Start(Household, ProductId, Clock);
        stock.AddStock(1000m, UnitId, Guid.CreateVersion7(), UserId, Clock);
        var converter = new IdentityQuantityConverter();
        stock.Consume(90m, UnitId, StockReason.Consumed, converter, UserId, Clock);
        stock.Consume(90m, UnitId, StockReason.Consumed, converter, UserId, Clock);
        stock.Consume(20m, UnitId, StockReason.Discarded, converter, UserId, Clock);
        var client = AuthClient(stock, []);

        var html = await (await client.GetAsync($"/Pantry/Products/Detail/{ProductId}"))
            .Content.ReadAsStringAsync();

        Assert.Contains("catalog-section__heading\">Stats<", html, StringComparison.Ordinal);
        Assert.Contains("days of supply left", html, StringComparison.Ordinal);
        Assert.Contains("Waste rate", html, StringComparison.Ordinal);
    }
}

internal sealed class ProductDetailStatsPanelFactory(
    ProductStock stock,
    IReadOnlyList<PriceObservation> priceHistory,
    CatalogUnit displayUnit,
    Guid? catalogDefaultUnitId,
    bool catalogProductExists)
    : WebApplicationFactory<Program>
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

            services.RemoveAll<IClock>();
            services.AddSingleton(ProductDetailStatsPanelTests.Clock);

            services.RemoveAll<IUnitRepository>();
            services.AddSingleton<IUnitRepository>(new FakeSingleUnitRepository(displayUnit));

            services.RemoveAll<ICatalogReadFacade>();
            services.AddSingleton<ICatalogReadFacade>(new FakeCatalogReadFacade(
                ProductDetailStatsPanelTestsProductId,
                displayUnit,
                catalogDefaultUnitId,
                catalogProductExists));

            var stockRepo = new FakeDetailStockRepository();
            stockRepo.Items.Add(stock);
            services.RemoveAll<IProductStockRepository>();
            services.AddSingleton<IProductStockRepository>(stockRepo);

            services.RemoveAll<IProductConversionProvider>();
            services.AddSingleton<IProductConversionProvider>(new IdentityConversionProvider());

            services.RemoveAll<IStockProvenanceReader>();
            services.AddSingleton<IStockProvenanceReader>(new FakeStockProvenanceReader());

            var priceRepo = new FakePriceObservationRepository();
            foreach (var observation in priceHistory)
                priceRepo.Items.Add(observation);
            services.RemoveAll<IPriceObservationRepository>();
            services.AddSingleton<IPriceObservationRepository>(priceRepo);

            services.RemoveAll<IDisplayCurrency>();
            services.AddSingleton<IDisplayCurrency>(new FakeDisplayCurrency());

            services.RemoveAll<IUnitPriceCalculator>();
            services.AddSingleton<IUnitPriceCalculator>(new FakeUnitPriceCalculator(0.5m));

            services.RemoveAll<Plantry.Recipes.Domain.IRecipeRepository>();
            services.AddSingleton<Plantry.Recipes.Domain.IRecipeRepository>(new FakeRecipeRepository());
        });
    }

    private static readonly Guid ProductDetailStatsPanelTestsHousehold = Guid.Parse("aaaaaaaa-1111-0000-0000-000000000001");
    private static readonly Guid ProductDetailStatsPanelTestsProductId = Guid.Parse("bbbbbbbb-1111-0000-0000-bbb000000001");
}
