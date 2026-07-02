using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.SharedKernel;
using Xunit;

namespace Plantry.Tests.Unit.Deals.Application;

/// <summary>
/// L2 tests for <see cref="StockUpAlerts"/> (P5-10 / DJ5). Proves the intersection — a product surfaces as
/// an alert only when it is BOTH frequently bought (purchase-journal threshold, via
/// <see cref="IPurchaseFrequencyReader"/>) AND currently has an active deal (Deals' own
/// <see cref="BrowseDeals"/> active partition). A frequent product with no active deal yields no alert, and
/// an active deal on a below-threshold product yields no alert. Also proves the cheapest-active-deal pick,
/// the frequency threshold boundary, the trailing window passed to the reader, and that everything is
/// recomputed read-time (nothing stored).
/// </summary>
public sealed class StockUpAlertsTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid Store = Guid.NewGuid();
    private static readonly Guid StoreB = Guid.NewGuid();

    private readonly FakeDealRepository _deals = new();
    private readonly FakeCatalogProductReader _products = new();
    private readonly FakeCatalogStoreReader _stores = new();
    private readonly FakePurchaseFrequencyReader _frequency = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));

    private StockUpAlerts Sut => new(new BrowseDeals(_deals, _products, _stores, _clock), _frequency, _clock);

    public StockUpAlertsTests()
    {
        _stores.Names[Store] = "FreshCo";
        _stores.Names[StoreB] = "Metro";
    }

    /// <summary>Stages a confirmed, in-window (active) deal for <paramref name="product"/> at the given price/store.</summary>
    private Deal StageActiveDeal(Guid product, decimal price, Guid? store = null)
    {
        var window = ValidityWindow.Create(new DateOnly(2026, 4, 28), new DateOnly(2026, 5, 7)).Value;
        var raw = new RawDeal("Deal", null, null, price, null, null, "Save", window);
        var deal = Deal.Stage(Household, FlyerImportId.New(), store ?? Store, raw,
            DealNormalizer.Normalize("Deal"), MatchProposal.Unmatched(), _clock);
        deal.Confirm(product, Guid.NewGuid(), _clock);
        _deals.Items.Add(deal);
        return deal;
    }

    [Fact(DisplayName = "Alert only when frequent ∩ active-deal: frequent-without-deal and deal-without-frequency both yield nothing")]
    public async Task Intersection_Requires_Both_Sides()
    {
        var frequentOnDeal = Guid.NewGuid();
        var frequentNoDeal = Guid.NewGuid();
        var infrequentOnDeal = Guid.NewGuid();
        _products.Products[frequentOnDeal] = new DealProductInfo(frequentOnDeal, "Whole Milk", "Dairy");
        _products.Products[infrequentOnDeal] = new DealProductInfo(infrequentOnDeal, "Truffle Oil", "Pantry");

        // frequentOnDeal: bought 4× AND on an active deal → alert.
        StageActiveDeal(frequentOnDeal, 3.99m);
        _frequency.Counts[frequentOnDeal] = 4;
        // frequentNoDeal: bought 5× but no active deal → no alert.
        _frequency.Counts[frequentNoDeal] = 5;
        // infrequentOnDeal: on an active deal but only bought once → no alert.
        StageActiveDeal(infrequentOnDeal, 9.99m);
        _frequency.Counts[infrequentOnDeal] = 1;

        var alerts = await Sut.ComputeAsync();

        var alert = Assert.Single(alerts);
        Assert.Equal(frequentOnDeal, alert.ProductId);
        Assert.Equal("Whole Milk", alert.ProductName);
        Assert.Equal(4, alert.PurchaseCount);
    }

    [Fact(DisplayName = "Frequency threshold is inclusive at the boundary: exactly the threshold qualifies, one below does not")]
    public async Task Threshold_Boundary_Is_Inclusive()
    {
        var atThreshold = Guid.NewGuid();
        var belowThreshold = Guid.NewGuid();
        _products.Products[atThreshold] = new DealProductInfo(atThreshold, "Eggs", null);
        _products.Products[belowThreshold] = new DealProductInfo(belowThreshold, "Butter", null);

        StageActiveDeal(atThreshold, 2.49m);
        StageActiveDeal(belowThreshold, 4.49m);
        _frequency.Counts[atThreshold] = StockUpAlerts.FrequencyThreshold;      // exactly 3 → qualifies
        _frequency.Counts[belowThreshold] = StockUpAlerts.FrequencyThreshold - 1; // 2 → excluded

        var alerts = await Sut.ComputeAsync();

        var alert = Assert.Single(alerts);
        Assert.Equal(atThreshold, alert.ProductId);
    }

    [Fact(DisplayName = "Carries the CHEAPEST active deal's store + price + window for a product with multiple active deals")]
    public async Task Picks_Cheapest_Active_Deal()
    {
        var product = Guid.NewGuid();
        _products.Products[product] = new DealProductInfo(product, "Coffee", "Beverages");

        StageActiveDeal(product, 12.99m, store: Store);         // pricier, FreshCo
        var cheapest = StageActiveDeal(product, 8.49m, store: StoreB); // cheapest, Metro
        _frequency.Counts[product] = 6;

        var alert = Assert.Single(await Sut.ComputeAsync());

        Assert.Equal(cheapest.Id, alert.DealId);
        Assert.Equal(8.49m, alert.Price);
        Assert.Equal(StoreB, alert.StoreId);
        Assert.Equal("Metro", alert.StoreName);
        Assert.Equal(new DateOnly(2026, 5, 7), alert.ValidTo);
    }

    [Fact(DisplayName = "Passes the trailing-window start to the frequency reader (today − FrequencyWindowDays)")]
    public async Task Queries_Frequency_Over_The_Trailing_Window()
    {
        var product = Guid.NewGuid();
        _products.Products[product] = new DealProductInfo(product, "Milk", null);
        StageActiveDeal(product, 3m);
        _frequency.Counts[product] = 3;

        await Sut.ComputeAsync();

        // today = 2026-05-01 → window start = 2026-05-01 − 120d = 2026-01-01.
        var expected = new DateTimeOffset(new DateOnly(2026, 1, 1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        Assert.Equal(expected, Assert.Single(_frequency.SinceCalls));
    }

    [Fact(DisplayName = "No active deals → no alerts, and the frequency reader is not even consulted")]
    public async Task No_Active_Deals_Short_Circuits()
    {
        // A frequent product exists, but there is no active deal to intersect it with.
        _frequency.Counts[Guid.NewGuid()] = 10;

        var alerts = await Sut.ComputeAsync();

        Assert.Empty(alerts);
        Assert.Empty(_frequency.SinceCalls); // short-circuited before the Inventory read
    }

    [Fact(DisplayName = "An active deal on an unresolved (pending) product is ignored — alerts need a resolved product")]
    public async Task Pending_Deals_Are_Not_Alertable()
    {
        // A pending deal (no committed product) plus frequency for some other product — no intersection.
        var window = ValidityWindow.Create(new DateOnly(2026, 4, 28), new DateOnly(2026, 5, 7)).Value;
        var raw = new RawDeal("Mystery Item", null, null, 1.99m, null, null, "Save", window);
        var pending = Deal.Stage(Household, FlyerImportId.New(), Store, raw,
            DealNormalizer.Normalize("Mystery Item"), MatchProposal.Unmatched(), _clock);
        _deals.Items.Add(pending); // left Pending — never confirmed

        _frequency.Counts[Guid.NewGuid()] = 8;

        Assert.Empty(await Sut.ComputeAsync());
    }
}

/// <summary>In-memory <see cref="IPurchaseFrequencyReader"/>; records the window-start instants it was asked for.</summary>
internal sealed class FakePurchaseFrequencyReader : IPurchaseFrequencyReader
{
    public Dictionary<Guid, int> Counts { get; } = new();
    public List<DateTimeOffset> SinceCalls { get; } = [];

    public Task<IReadOnlyDictionary<Guid, int>> PurchaseCountsSinceAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        SinceCalls.Add(since);
        IReadOnlyDictionary<Guid, int> result = new Dictionary<Guid, int>(Counts);
        return Task.FromResult(result);
    }
}
