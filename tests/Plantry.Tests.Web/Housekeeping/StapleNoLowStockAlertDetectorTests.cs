using Plantry.Housekeeping.Domain;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Housekeeping;
using Xunit;

namespace Plantry.Tests.Web.Housekeeping;

/// <summary>
/// L2 golden tests for <see cref="StapleNoLowStockAlertDetector"/> (D4, tidy-up.md §3) — including the
/// "exactly 3 distinct purchase dates" boundary (2 does not fire, 3 does), the null-<c>PurchasedAt</c>
/// exclusion, the 90-day lookback window, and fingerprint pinning: D4's gap is binary, so the fingerprint
/// is constant per subject regardless of the underlying facts (§4).
///
/// Tests live in Plantry.Tests.Web because the detector is in Plantry.Composition (referenced
/// transitively via Plantry.Web) — mirrors <c>ShoppingPantryReaderAdapterTests</c>' rationale.
/// </summary>
public sealed class StapleNoLowStockAlertDetectorTests
{
    private static readonly Guid HouseholdGuid = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000d4");
    private static readonly HouseholdId Household = HouseholdId.From(HouseholdGuid);

    private static readonly Guid MilkId = Guid.Parse("11111111-1111-1111-1111-0000000000d4");
    private static readonly Guid EachId = Guid.Parse("22222222-2222-2222-2222-0000000000d4");

    private static readonly IClock Clock = new TestClock(new DateOnly(2026, 7, 22));

    private static StapleNoLowStockAlertDetector BuildDetector(
        IProductStockRepository stocks, ICatalogReadFacade catalog, ITenantContext? tenant = null) =>
        new(stocks, catalog, Clock, tenant ?? new FakeD4TenantContext(HouseholdGuid));

    private static ICatalogReadFacade CatalogWithMilk()
    {
        var catalog = new FakeD4CatalogFacade();
        catalog.AddProduct(MilkId, "Milk", EachId, "ea");
        return catalog;
    }

    private static ProductStock MakeStockWithPurchases(params DateOnly?[] purchaseDates)
    {
        var stock = ProductStock.Start(Household, MilkId, Clock);
        foreach (var date in purchaseDates)
            stock.AddStock(1m, EachId, Guid.NewGuid(), Guid.NewGuid(), Clock, purchasedAt: date);
        return stock;
    }

    [Fact(DisplayName = "3 distinct purchase dates within 90 days, no threshold — produces a finding")]
    public async Task ThreeDistinctDates_NoThreshold_ProducesFinding()
    {
        var stock = MakeStockWithPurchases(
            new DateOnly(2026, 7, 1), new DateOnly(2026, 6, 15), new DateOnly(2026, 5, 25));
        var stocks = new FakeD4StockRepository();
        stocks.Add(stock);

        var findings = await BuildDetector(stocks, CatalogWithMilk()).DetectAsync();

        var finding = Assert.Single(findings);
        Assert.Equal(DetectorId.StapleNoLowStockAlert, finding.DetectorId);
        Assert.Equal(MilkId, finding.SubjectId);
        Assert.Equal("Milk", finding.SubjectName);
        Assert.Contains("3", finding.Specifics);
        Assert.Equal("/Pantry/Products/Detail/" + MilkId, finding.FixUrl);
        Assert.Equal("Set alert in Pantry", finding.FixLabel);
    }

    [Fact(DisplayName = "Boundary: exactly 2 distinct purchase dates — does NOT fire")]
    public async Task TwoDistinctDates_DoesNotFire()
    {
        var stock = MakeStockWithPurchases(new DateOnly(2026, 7, 1), new DateOnly(2026, 6, 15));
        var stocks = new FakeD4StockRepository();
        stocks.Add(stock);

        var findings = await BuildDetector(stocks, CatalogWithMilk()).DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "Threshold already set — never flagged even with frequent purchases")]
    public async Task ThresholdSet_NeverFlagged()
    {
        var stock = MakeStockWithPurchases(
            new DateOnly(2026, 7, 1), new DateOnly(2026, 6, 15), new DateOnly(2026, 5, 25));
        stock.SetLowStockThreshold(2m, Clock);
        var stocks = new FakeD4StockRepository();
        stocks.Add(stock);

        var findings = await BuildDetector(stocks, CatalogWithMilk()).DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "Null PurchasedAt entries are ignored — do not count toward the distinct-date total")]
    public async Task NullPurchasedAt_Ignored()
    {
        // Two real dates + two null-dated entries: total entries = 4, but only 2 distinct real dates —
        // below the threshold, so this must not fire even though the entry count alone would suggest it.
        var stock = MakeStockWithPurchases(
            new DateOnly(2026, 7, 1), new DateOnly(2026, 6, 15), null, null);
        var stocks = new FakeD4StockRepository();
        stocks.Add(stock);

        var findings = await BuildDetector(stocks, CatalogWithMilk()).DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "Purchase dates outside the 90-day lookback window are excluded")]
    public async Task OutsideLookbackWindow_Excluded()
    {
        // Today is 2026-07-22; 90 days back is 2026-04-23. Two of these three dates fall outside the
        // window, leaving only 1 in-window distinct date — below the threshold.
        var stock = MakeStockWithPurchases(
            new DateOnly(2026, 7, 1), new DateOnly(2026, 1, 1), new DateOnly(2025, 12, 1));
        var stocks = new FakeD4StockRepository();
        stocks.Add(stock);

        var findings = await BuildDetector(stocks, CatalogWithMilk()).DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "Depleted entries still count toward purchase frequency")]
    public async Task DepletedEntries_StillCount()
    {
        var userId = Guid.NewGuid();
        var stock = ProductStock.Start(Household, MilkId, Clock);
        var e1 = stock.AddStock(1m, EachId, Guid.NewGuid(), userId, Clock, purchasedAt: new DateOnly(2026, 7, 1));
        var e2 = stock.AddStock(1m, EachId, Guid.NewGuid(), userId, Clock, purchasedAt: new DateOnly(2026, 6, 15));
        stock.AddStock(1m, EachId, Guid.NewGuid(), userId, Clock, purchasedAt: new DateOnly(2026, 5, 25));
        var converter = new PassthroughConverter();
        stock.Consume(1m, EachId, StockReason.Consumed, converter, userId, Clock, targetEntry: e1.Id);
        stock.Consume(1m, EachId, StockReason.Consumed, converter, userId, Clock, targetEntry: e2.Id);
        var stocks = new FakeD4StockRepository();
        stocks.Add(stock);

        var findings = await BuildDetector(stocks, CatalogWithMilk()).DetectAsync();

        Assert.Single(findings); // all 3 purchase dates still count despite 2 lots now being depleted
    }

    [Fact(DisplayName = "No tenant — returns no findings")]
    public async Task NoTenant_ReturnsEmpty()
    {
        var stock = MakeStockWithPurchases(
            new DateOnly(2026, 7, 1), new DateOnly(2026, 6, 15), new DateOnly(2026, 5, 25));
        var stocks = new FakeD4StockRepository();
        stocks.Add(stock);

        var findings = await BuildDetector(stocks, CatalogWithMilk(), new FakeD4TenantContext(null)).DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "Fingerprint pinning: constant regardless of how many distinct dates or products differ")]
    public async Task Fingerprint_ConstantAcrossDifferentFactPatterns()
    {
        var stockA = MakeStockWithPurchases(
            new DateOnly(2026, 7, 1), new DateOnly(2026, 6, 15), new DateOnly(2026, 5, 25));
        var stocksA = new FakeD4StockRepository();
        stocksA.Add(stockA);
        var findingA = Assert.Single(await BuildDetector(stocksA, CatalogWithMilk()).DetectAsync());

        var stockB = MakeStockWithPurchases(
            new DateOnly(2026, 7, 10), new DateOnly(2026, 6, 1), new DateOnly(2026, 5, 1), new DateOnly(2026, 4, 25));
        var stocksB = new FakeD4StockRepository();
        stocksB.Add(stockB);
        var findingB = Assert.Single(await BuildDetector(stocksB, CatalogWithMilk()).DetectAsync());

        Assert.Equal(findingA.FactsFingerprint, findingB.FactsFingerprint);
    }
}

// ── Test doubles ─────────────────────────────────────────────────────────────────────────────────

file sealed class TestClock(DateOnly today) : IClock
{
    public DateTimeOffset UtcNow { get; } = new(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}

file sealed class PassthroughConverter : IQuantityConverter
{
    public Plantry.SharedKernel.Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId) => amount;
}

file sealed class FakeD4TenantContext(Guid? householdId) : ITenantContext
{
    public Guid? HouseholdId { get; } = householdId;
}

file sealed class FakeD4StockRepository : IProductStockRepository
{
    private readonly List<ProductStock> _stocks = [];
    public void Add(ProductStock stock) => _stocks.Add(stock);

    public Task<List<ProductStock>> ListForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(_stocks.Where(s => s.HouseholdId == householdId).ToList());

    public Task<ProductStock?> FindAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default) =>
        Task.FromResult(_stocks.SingleOrDefault(s => s.HouseholdId == householdId && s.ProductId == productId));

    public Task<ProductStock?> FindForUpdateAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default) =>
        FindAsync(householdId, productId, ct);

    public Task<ProductStock?> FindWithHistoryAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default) =>
        FindAsync(householdId, productId, ct);

    public Task AddAsync(ProductStock stock, CancellationToken ct = default) { _stocks.Add(stock); return Task.CompletedTask; }
    public Task<bool> TryAddAndSaveAsync(ProductStock stock, CancellationToken ct = default) { _stocks.Add(stock); return Task.FromResult(true); }
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(_stocks.Any(s => s.HouseholdId == householdId));

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default) =>
        await work(ct);
}

file sealed class FakeD4CatalogFacade : ICatalogReadFacade
{
    private readonly List<CatalogProductInfo> _products = [];

    public void AddProduct(Guid id, string name, Guid defaultUnitId, string defaultUnitCode) =>
        _products.Add(new CatalogProductInfo(id, name, null, defaultUnitId, defaultUnitCode, CanHoldStock: true));

    public Task<CatalogProductInfo?> FindProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(_products.SingleOrDefault(p => p.Id == productId));

    public Task<IReadOnlyList<CatalogProductInfo>> ListProductsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogProductInfo>>(_products);

    public Task<IReadOnlyDictionary<Guid, string>> GetUnitCodesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

    public Task<IReadOnlyDictionary<Guid, string>> GetLocationNamesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
}
