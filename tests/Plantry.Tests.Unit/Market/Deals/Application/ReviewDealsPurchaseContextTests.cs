using Plantry.Market.Application;
using Plantry.Market.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Unit.Market;
using Xunit;

namespace Plantry.Tests.Unit.Market.Deals.Application;

/// <summary>
/// L2 tests for the Deals-review purchase-context injection (plantry-gtgl, stats-page-prototype.html
/// appendix "Deals review" — "you pay $X avg → deal is Y% below/above", "you buy this every ~N", "last
/// bought"). Proves the batching (one price-history read, one purchase-dates read, one latest-purchase read
/// for the whole queue, never per-card), the silent skip when the suggested product has no purchase history,
/// and the percent-delta sign convention (negative == the deal undercuts the household's average).
/// </summary>
public sealed class ReviewDealsPurchaseContextTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid Store = Guid.NewGuid();
    private static readonly Guid Milk = Guid.NewGuid();
    private static readonly Guid Bread = Guid.NewGuid();
    private static readonly Guid GramUnit = Guid.NewGuid();
    private static readonly TestClock Clock = new(new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero));
    private static readonly DateOnly Today = new(2026, 8, 1);

    private readonly FakeDealRepository _deals = new();
    private readonly FakeCatalogProductReader _products = new();
    private readonly FakeCatalogStoreReader _stores = new();
    private readonly FakeFlyerImportRepository _flyerImports = new();
    private readonly FakePriceObservationRepository _observations = new();
    private readonly FakePurchaseFrequencyReader _frequency = new();

    private ReviewDeals Sut(decimal? normalizedDealUnitPrice) =>
        Sut(new FakeUnitPriceCalculator(normalizedDealUnitPrice));

    private ReviewDeals Sut(FakeUnitPriceCalculator calculator) => new(
        _deals, _products, _stores, _flyerImports, Clock,
        new PricingQueries(_observations), _frequency, calculator);

    public ReviewDealsPurchaseContextTests()
    {
        _stores.Names[Store] = "FreshCo";
        _products.Products[Milk] = new DealProductInfo(Milk, "Whole Milk", "Dairy");
        _products.Products[Bread] = new DealProductInfo(Bread, "Sourdough", "Bakery");
    }

    private Deal StagePending(Guid storeId, Guid product, decimal price = 4.99m, decimal? quantity = 1m, Guid? unitId = null, bool unitless = false)
    {
        var window = ValidityWindow.Create(Today.AddDays(-1), Today.AddDays(6)).Value;
        var raw = new RawDeal("Some Deal", "Brand", null, price, quantity, unitless ? null : (unitId ?? GramUnit), "Save $1", window);
        var deal = Deal.Stage(
            Household, FlyerImportId.New(), storeId, raw, DealNormalizer.Normalize("Some Deal"),
            new MatchProposal(product, MatchConfidence.High, "looks like a match"), Clock);
        _deals.Items.Add(deal);
        return deal;
    }

    private static PriceObservation Purchase(Guid productId, decimal unitPrice, DateTimeOffset observedAt) =>
        PriceObservation.Record(
            Household, productId, null, unitPrice, 1m, GramUnit, unitPrice,
            PriceSource.Purchase, "SomeStore", null, observedAt, Guid.NewGuid());

    [Fact(DisplayName = "A suggested product with purchase history gets avg price, delta, cadence, and last-bought date")]
    public async Task Builds_Full_Purchase_Context()
    {
        StagePending(Store, Milk, price: 5.00m);
        _observations.Items.Add(Purchase(Milk, 4.00m, new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)));
        _observations.Items.Add(Purchase(Milk, 6.00m, new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero)));
        _frequency.Dates[Milk] = [
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero),
        ];

        var views = await Sut(normalizedDealUnitPrice: 4.50m).ListPendingAsync();

        var context = Assert.Single(views).Purchase;
        Assert.NotNull(context);
        Assert.Equal(5.00m, context!.AverageUnitPrice); // (4.00 + 6.00) / 2
        Assert.Equal(4.50m, context.DealUnitPrice);
        Assert.Equal(-10.0m, context.PercentDelta); // (4.50 - 5.00) / 5.00 * 100
        Assert.Equal(TimeSpan.FromDays(21), context.AveragePurchaseInterval);
        Assert.Equal(new DateOnly(2026, 7, 22), context.LastPurchasedAt);
    }

    [Fact(DisplayName = "A deal price above the household average yields a positive percent delta")]
    public async Task Deal_Above_Average_Is_Positive_Delta()
    {
        StagePending(Store, Milk);
        _observations.Items.Add(Purchase(Milk, 4.00m, new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)));

        var views = await Sut(normalizedDealUnitPrice: 5.00m).ListPendingAsync();

        var context = Assert.Single(views).Purchase;
        Assert.Equal(25.0m, context!.PercentDelta); // (5.00 - 4.00) / 4.00 * 100
    }

    [Fact(DisplayName = "A suggested product with no purchase history at all is skipped silently (no context)")]
    public async Task No_Purchase_History_Skips_Silently()
    {
        StagePending(Store, Milk);

        var views = await Sut(normalizedDealUnitPrice: 4.50m).ListPendingAsync();

        Assert.Null(Assert.Single(views).Purchase);
    }

    [Fact(DisplayName = "A single purchase yields an avg price and last-bought date but no cadence (no interval to measure)")]
    public async Task Single_Purchase_Has_No_Cadence()
    {
        StagePending(Store, Milk);
        _observations.Items.Add(Purchase(Milk, 4.00m, new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)));

        var views = await Sut(normalizedDealUnitPrice: 4.50m).ListPendingAsync();

        var context = Assert.Single(views).Purchase;
        Assert.NotNull(context);
        Assert.Equal(4.00m, context!.AverageUnitPrice);
        Assert.Null(context.AveragePurchaseInterval);
    }

    [Fact(DisplayName = "A deal that never carried a unit still shows the avg price/cadence, with no percent delta and no normalization call at all")]
    public async Task Unitless_Deal_Has_No_Percent_Delta()
    {
        StagePending(Store, Milk, quantity: null, unitless: true);
        _observations.Items.Add(Purchase(Milk, 4.00m, new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)));

        var calculator = new FakeUnitPriceCalculator(returnValue: null);
        var views = await Sut(calculator).ListPendingAsync();

        var context = Assert.Single(views).Purchase;
        Assert.NotNull(context);
        Assert.Equal(4.00m, context!.AverageUnitPrice);
        Assert.Null(context.DealUnitPrice);
        Assert.Null(context.PercentDelta);
        // The view.UnitId-is-null arm must skip the calculator entirely — collapsing the conditional into
        // an unguarded normalize call (an NRE hazard on a genuinely unit-less deal) turns this red.
        Assert.Equal(0, calculator.NormalizeCalls);
    }

    [Fact(DisplayName = "A deal that carries a unit normalizes through the calculator exactly once")]
    public async Task Deal_With_A_Unit_Normalizes_Through_The_Calculator()
    {
        StagePending(Store, Milk, price: 5.00m);
        _observations.Items.Add(Purchase(Milk, 4.00m, new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)));

        var calculator = new FakeUnitPriceCalculator(returnValue: 4.50m);
        var views = await Sut(calculator).ListPendingAsync();

        var context = Assert.Single(views).Purchase;
        Assert.NotNull(context);
        Assert.Equal(4.50m, context!.DealUnitPrice);
        Assert.Equal(1, calculator.NormalizeCalls); // together with the unitless fact, pins both arms of view.UnitId is { }
    }

    [Fact(DisplayName = "An unmatched (None-confidence) deal with no suggested product never gets a purchase context")]
    public async Task Unmatched_Deal_Has_No_Purchase_Context()
    {
        var window = ValidityWindow.Create(Today.AddDays(-1), Today.AddDays(6)).Value;
        var raw = new RawDeal("Mystery", null, null, 1.99m, null, null, "Save", window);
        var deal = Deal.Stage(
            Household, FlyerImportId.New(), Store, raw, DealNormalizer.Normalize("Mystery"),
            MatchProposal.Unmatched(), Clock);
        _deals.Items.Add(deal);
        _observations.Items.Add(Purchase(Milk, 4.00m, new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)));

        var views = await Sut(normalizedDealUnitPrice: null).ListPendingAsync();

        Assert.Null(Assert.Single(views).Purchase);
    }

    [Fact(DisplayName = "Multiple pending deals batch their purchase reads instead of one round trip per card")]
    public async Task Batches_Purchase_Reads_Across_The_Queue()
    {
        StagePending(Store, Milk, price: 5.00m);
        StagePending(Store, Bread, price: 3.00m);
        _observations.Items.Add(Purchase(Milk, 4.00m, new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)));
        _observations.Items.Add(Purchase(Bread, 2.50m, new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)));

        var views = await Sut(normalizedDealUnitPrice: 4.50m).ListPendingAsync();

        Assert.Equal(2, views.Count);
        Assert.All(views, v => Assert.NotNull(v.Purchase));
        // The fakes override the interfaces' per-product DIM loops with call-counting batch reads
        // (plantry-gtgl pass-3 critic): exactly one whole-queue call each — removing the batching (or
        // regressing to the per-product default) turns these red instead of silently passing.
        Assert.Equal(1, _observations.HistoryForProductsCalls);
        Assert.Equal(1, _observations.LatestForProductsCalls);
        Assert.Equal(1, _frequency.PurchaseDatesCalls);
    }

    [Fact(DisplayName = "A zero average unit price (a $0.00 free/promo purchase history) skips the context instead of dividing by zero")]
    public async Task Zero_Average_Price_Skips_Context_Instead_Of_Throwing()
    {
        StagePending(Store, Milk, price: 5.00m);
        _observations.Items.Add(Purchase(Milk, 0.00m, new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)));

        var views = await Sut(normalizedDealUnitPrice: 4.50m).ListPendingAsync();

        Assert.Null(Assert.Single(views).Purchase);
    }

    [Fact(DisplayName = "FindAsync includes the purchase context by default (the single-correction card path)")]
    public async Task FindAsync_Includes_Purchase_Context_By_Default()
    {
        var deal = StagePending(Store, Milk, price: 5.00m);
        _observations.Items.Add(Purchase(Milk, 4.00m, new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)));

        var view = await Sut(normalizedDealUnitPrice: 4.50m).FindAsync(deal.Id);

        Assert.NotNull(view);
        Assert.NotNull(view!.Purchase);
    }

    [Fact(DisplayName = "FindAsync(includePurchaseContext: false) skips the purchase-context round trips (the Confirm action path)")]
    public async Task FindAsync_Skips_Purchase_Context_When_Excluded()
    {
        var deal = StagePending(Store, Milk, price: 5.00m);
        _observations.Items.Add(Purchase(Milk, 4.00m, new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)));

        var view = await Sut(normalizedDealUnitPrice: 4.50m).FindAsync(deal.Id, includePurchaseContext: false);

        Assert.NotNull(view);
        Assert.Null(view!.Purchase);
        Assert.Equal(Milk, view.SuggestedProductId); // the field Confirm actually needs is still present
    }
}
