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
    private static readonly IClock Clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
    private static readonly Guid ProductId = Guid.Parse("bbbbbbbb-1111-0000-0000-bbb000000001");
    private static readonly Guid UnitId = Guid.Parse("cccccccc-1111-0000-0000-ccc000000001");
    private static readonly Guid UserId = Guid.Parse("dddddddd-1111-0000-0000-000000000aa1");

    private ProductDetailStatsPanelFactory? _factory;

    public void Dispose() => _factory?.Dispose();

    private HttpClient AuthClient(ProductStock stock, IReadOnlyList<PriceObservation> priceHistory)
    {
        _factory = new ProductDetailStatsPanelFactory(stock, priceHistory);
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdId.ToString());
        return client;
    }

    private static CatalogUnit Unit() => CatalogUnit.Create(Household, "g", "Grams", Dimension.Mass, 1m, isBase: true);

    private static PriceObservation Purchase(decimal unitPrice, DateTimeOffset observedAt) =>
        PriceObservation.Record(
            Household, ProductId, null, price: unitPrice, quantity: 1m, unitId: UnitId, unitPrice: unitPrice,
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
            Purchase(3.00m, DateTimeOffset.UtcNow.AddDays(-30)),
            Purchase(5.00m, DateTimeOffset.UtcNow.AddDays(-1)),
        };
        var client = AuthClient(stock, priceHistory);

        var html = await (await client.GetAsync($"/Pantry/Products/Detail/{ProductId}"))
            .Content.ReadAsStringAsync();

        Assert.Contains("catalog-section__heading\">Stats<", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Price history trend\"", html, StringComparison.Ordinal);
        // Median of {3.00, 5.00} is 4.00.
        Assert.Contains("4.00", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Detail GET — omits the sparkline with only a single usable price point, but a single point still doesn't crash the page")]
    public async Task Get_OmitsSparkline_WithOnlyOnePricePoint()
    {
        var stock = ProductStock.Start(Household, ProductId, Clock);
        stock.AddStock(100m, UnitId, Guid.CreateVersion7(), UserId, Clock);
        var client = AuthClient(stock, [Purchase(3.00m, DateTimeOffset.UtcNow)]);

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

internal sealed class ProductDetailStatsPanelFactory(ProductStock stock, IReadOnlyList<PriceObservation> priceHistory)
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

            var unit = CatalogUnit.Create(
                Plantry.SharedKernel.HouseholdId.From(ProductDetailStatsPanelTestsHousehold), "g", "Grams", Dimension.Mass, 1m, isBase: true);

            services.RemoveAll<IUnitRepository>();
            services.AddSingleton<IUnitRepository>(new FakeSingleUnitRepository(unit));

            services.RemoveAll<ICatalogReadFacade>();
            services.AddSingleton<ICatalogReadFacade>(new FakeCatalogReadFacade(ProductDetailStatsPanelTestsProductId, unit));

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
