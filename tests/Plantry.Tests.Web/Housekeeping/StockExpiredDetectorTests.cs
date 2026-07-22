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
/// L2 golden tests for <see cref="StockExpiredDetector"/> (D3, tidy-up.md §3) — including the 0-day
/// grace window boundary (expiring today does NOT fire) and fingerprint pinning: the fingerprint covers
/// only the expired lot ids, never quantities, so partially consuming an already-expired lot must not
/// change it (an accidental fingerprint change would mass-reopen every dismissed D3 finding, §4).
///
/// Tests live in Plantry.Tests.Web because the detector is in Plantry.Composition (referenced
/// transitively via Plantry.Web) — mirrors <c>ShoppingPantryReaderAdapterTests</c>' rationale.
/// </summary>
public sealed class StockExpiredDetectorTests
{
    private static readonly Guid HouseholdGuid = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000d3");
    private static readonly HouseholdId Household = HouseholdId.From(HouseholdGuid);

    private static readonly Guid YogurtId = Guid.Parse("11111111-1111-1111-1111-0000000000d3");
    private static readonly Guid EachId = Guid.Parse("22222222-2222-2222-2222-0000000000d3");

    private static readonly IClock Clock = new TestClock(new DateOnly(2026, 7, 22));

    private static StockExpiredDetector BuildDetector(
        IProductStockRepository stocks, ICatalogReadFacade catalog, IClock? clock = null, ITenantContext? tenant = null) =>
        new(stocks, catalog, clock ?? Clock, tenant ?? new FakeD3TenantContext(HouseholdGuid));

    private static ICatalogReadFacade CatalogWithYogurt()
    {
        var catalog = new FakeD3CatalogFacade();
        catalog.AddProduct(YogurtId, "Yogurt", EachId, "ea");
        return catalog;
    }

    [Fact(DisplayName = "Active lot expired before today — produces a finding")]
    public async Task ExpiredLot_ProducesFinding()
    {
        var stock = ProductStock.Start(Household, YogurtId, Clock);
        stock.AddStock(2m, EachId, Guid.NewGuid(), Guid.NewGuid(), Clock, expiryDate: new DateOnly(2026, 7, 1));
        var stocks = new FakeD3StockRepository();
        stocks.Add(stock);

        var findings = await BuildDetector(stocks, CatalogWithYogurt()).DetectAsync();

        var finding = Assert.Single(findings);
        Assert.Equal(DetectorId.StockExpired, finding.DetectorId);
        Assert.Equal(YogurtId, finding.SubjectId);
        Assert.Equal("Yogurt", finding.SubjectName);
        Assert.Equal("1 lot expired 2026-07-01", finding.Specifics);
        Assert.Equal("/Pantry/Products/Detail/" + YogurtId, finding.FixUrl);
        Assert.Equal("Review in Pantry", finding.FixLabel);
    }

    [Fact(DisplayName = "Lot expiring exactly today — 0-day grace window, does NOT fire")]
    public async Task ExpiresToday_DoesNotFire()
    {
        var stock = ProductStock.Start(Household, YogurtId, Clock);
        stock.AddStock(2m, EachId, Guid.NewGuid(), Guid.NewGuid(), Clock, expiryDate: new DateOnly(2026, 7, 22));
        var stocks = new FakeD3StockRepository();
        stocks.Add(stock);

        var findings = await BuildDetector(stocks, CatalogWithYogurt()).DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "Lot expiring tomorrow — does not fire")]
    public async Task ExpiresTomorrow_DoesNotFire()
    {
        var stock = ProductStock.Start(Household, YogurtId, Clock);
        stock.AddStock(2m, EachId, Guid.NewGuid(), Guid.NewGuid(), Clock, expiryDate: new DateOnly(2026, 7, 23));
        var stocks = new FakeD3StockRepository();
        stocks.Add(stock);

        var findings = await BuildDetector(stocks, CatalogWithYogurt()).DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "No expiry date on the lot — never flagged")]
    public async Task NoExpiryDate_DoesNotFire()
    {
        var stock = ProductStock.Start(Household, YogurtId, Clock);
        stock.AddStock(2m, EachId, Guid.NewGuid(), Guid.NewGuid(), Clock);
        var stocks = new FakeD3StockRepository();
        stocks.Add(stock);

        var findings = await BuildDetector(stocks, CatalogWithYogurt()).DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "Multiple expired lots — Specifics reports the count and the oldest expiry")]
    public async Task MultipleExpiredLots_ReportsCountAndOldest()
    {
        var stock = ProductStock.Start(Household, YogurtId, Clock);
        stock.AddStock(1m, EachId, Guid.NewGuid(), Guid.NewGuid(), Clock, expiryDate: new DateOnly(2026, 7, 10));
        stock.AddStock(1m, EachId, Guid.NewGuid(), Guid.NewGuid(), Clock, expiryDate: new DateOnly(2026, 6, 1));
        var stocks = new FakeD3StockRepository();
        stocks.Add(stock);

        var finding = Assert.Single(await BuildDetector(stocks, CatalogWithYogurt()).DetectAsync());

        Assert.Equal("2 lots expired, oldest 2026-06-01", finding.Specifics);
    }

    [Fact(DisplayName = "Depleted lot past expiry — not active, does not fire")]
    public async Task DepletedExpiredLot_DoesNotFire()
    {
        var userId = Guid.NewGuid();
        var stock = ProductStock.Start(Household, YogurtId, Clock);
        var entry = stock.AddStock(1m, EachId, Guid.NewGuid(), userId, Clock, expiryDate: new DateOnly(2026, 7, 1));
        stock.Consume(1m, EachId, StockReason.Consumed, new IdentityConverter(), userId, Clock, targetEntry: entry.Id);
        var stocks = new FakeD3StockRepository();
        stocks.Add(stock);

        var findings = await BuildDetector(stocks, CatalogWithYogurt()).DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "No tenant — returns no findings")]
    public async Task NoTenant_ReturnsEmpty()
    {
        var stock = ProductStock.Start(Household, YogurtId, Clock);
        stock.AddStock(2m, EachId, Guid.NewGuid(), Guid.NewGuid(), Clock, expiryDate: new DateOnly(2026, 7, 1));
        var stocks = new FakeD3StockRepository();
        stocks.Add(stock);

        var findings = await BuildDetector(stocks, CatalogWithYogurt(), tenant: new FakeD3TenantContext(null)).DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "Fingerprint pinning: consuming part of an already-expired lot does NOT change the fingerprint")]
    public async Task Fingerprint_UnaffectedByPartialConsume()
    {
        var userId = Guid.NewGuid();
        var stock = ProductStock.Start(Household, YogurtId, Clock);
        var entry = stock.AddStock(5m, EachId, Guid.NewGuid(), userId, Clock, expiryDate: new DateOnly(2026, 7, 1));
        var stocks = new FakeD3StockRepository();
        stocks.Add(stock);

        var findingBefore = Assert.Single(await BuildDetector(stocks, CatalogWithYogurt()).DetectAsync());

        // Same StockEntry (still active, just fewer units remaining) — the fingerprint is built from the
        // expired entry id set, not quantity, so it must not change.
        stock.Consume(3m, EachId, StockReason.Consumed, new IdentityConverter(), userId, Clock, targetEntry: entry.Id);
        var findingAfter = Assert.Single(await BuildDetector(stocks, CatalogWithYogurt()).DetectAsync());

        Assert.Equal(findingBefore.FactsFingerprint, findingAfter.FactsFingerprint);
    }

    [Fact(DisplayName = "Fingerprint pinning: a newly-expired lot changes the fingerprint (reopens dismissal)")]
    public async Task Fingerprint_ChangesWhenAnotherLotExpires()
    {
        var stockOne = ProductStock.Start(Household, YogurtId, Clock);
        stockOne.AddStock(1m, EachId, Guid.NewGuid(), Guid.NewGuid(), Clock, expiryDate: new DateOnly(2026, 7, 1));
        var stocksOne = new FakeD3StockRepository();
        stocksOne.Add(stockOne);
        var findingOne = Assert.Single(await BuildDetector(stocksOne, CatalogWithYogurt()).DetectAsync());

        var stockTwo = ProductStock.Start(Household, YogurtId, Clock);
        stockTwo.AddStock(1m, EachId, Guid.NewGuid(), Guid.NewGuid(), Clock, expiryDate: new DateOnly(2026, 7, 1));
        stockTwo.AddStock(1m, EachId, Guid.NewGuid(), Guid.NewGuid(), Clock, expiryDate: new DateOnly(2026, 6, 1));
        var stocksTwo = new FakeD3StockRepository();
        stocksTwo.Add(stockTwo);
        var findingTwo = Assert.Single(await BuildDetector(stocksTwo, CatalogWithYogurt()).DetectAsync());

        Assert.NotEqual(findingOne.FactsFingerprint, findingTwo.FactsFingerprint);
    }
}

// ── Test doubles ─────────────────────────────────────────────────────────────────────────────────

file sealed class TestClock(DateOnly today) : IClock
{
    public DateTimeOffset UtcNow { get; } = new(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}

/// <summary>Always succeeds when converting within the same unit — sufficient for the Consume calls this
/// test file makes (same-unit consume in every case).</summary>
file sealed class IdentityConverter : IQuantityConverter
{
    public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId) =>
        fromUnitId == toUnitId
            ? amount
            : Result<decimal>.Failure(Error.Custom("Test.NoConversion", "No conversion factor."));
}

file sealed class FakeD3TenantContext(Guid? householdId) : ITenantContext
{
    public Guid? HouseholdId { get; } = householdId;
}

file sealed class FakeD3StockRepository : IProductStockRepository
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

file sealed class FakeD3CatalogFacade : ICatalogReadFacade
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
