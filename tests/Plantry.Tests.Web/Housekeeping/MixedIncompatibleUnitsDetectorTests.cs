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
/// L2 golden tests for <see cref="MixedIncompatibleUnitsDetector"/> (D6, tidy-up.md §3) — pinning that
/// it fires exactly when <see cref="InventoryQueryService.DisplayQuantity"/> would fall back to its
/// <c>"?"</c> unit code, and that a genuinely convertible pair of units does NOT fire even though it also
/// differs from the display unit (the D1 case, not D6's). Fingerprint pinning mirrors D1's discipline:
/// sorted distinct active-lot unit ids + the display unit id, never quantities (§4).
///
/// Tests live in Plantry.Tests.Web because the detector is in Plantry.Composition (referenced
/// transitively via Plantry.Web) — mirrors <c>ShoppingPantryReaderAdapterTests</c>' rationale.
/// </summary>
public sealed class MixedIncompatibleUnitsDetectorTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly Guid HouseholdGuid = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000d6");
    private static readonly HouseholdId Household = HouseholdId.From(HouseholdGuid);

    private static readonly Guid OnionId = Guid.Parse("11111111-1111-1111-1111-0000000000d8");
    private static readonly Guid EachId = Guid.Parse("22222222-2222-2222-2222-0000000000d8");
    private static readonly Guid PoundId = Guid.Parse("22222222-2222-2222-2222-0000000000d9");
    private static readonly Guid GramId = Guid.Parse("22222222-2222-2222-2222-0000000000da");

    private static MixedIncompatibleUnitsDetector BuildDetector(
        IProductStockRepository stocks, ICatalogReadFacade catalog, IProductConversionProvider conversions,
        ITenantContext? tenant = null) =>
        new(stocks, catalog, conversions, tenant ?? new FakeD6TenantContext(HouseholdGuid));

    [Fact(DisplayName = "Two units, neither convertible to the display unit — fires (DisplayQuantity falls back to \"?\")")]
    public async Task TwoIncompatibleUnits_ProducesFinding()
    {
        var stock = ProductStock.Start(Household, OnionId, Clock);
        stock.AddStock(3m, PoundId, Guid.NewGuid(), Guid.NewGuid(), Clock);
        stock.AddStock(2m, GramId, Guid.NewGuid(), Guid.NewGuid(), Clock);
        var stocks = new FakeD6StockRepository();
        stocks.Add(stock);

        var catalog = new FakeD6CatalogFacade();
        catalog.AddProduct(OnionId, "Onion Yellow", EachId, "ea");
        catalog.AddUnitCode(PoundId, "lb");
        catalog.AddUnitCode(GramId, "g");

        // Neither lb nor g converts to "ea", and lb doesn't convert to g either — the mixed-incompatible case.
        var detector = BuildDetector(stocks, catalog, new FakeD6ConversionProvider(mismatch: true));
        var findings = await detector.DetectAsync();

        var finding = Assert.Single(findings);
        Assert.Equal(DetectorId.StockMixedIncompatibleUnits, finding.DetectorId);
        Assert.Equal(OnionId, finding.SubjectId);
        Assert.Equal("Onion Yellow", finding.SubjectName);
        Assert.Contains("lb", finding.Specifics);
        Assert.Contains("g", finding.Specifics);
        Assert.Equal("/Catalog/Products/" + OnionId + "#conversions", finding.FixUrl);
        Assert.Equal("Fix in Catalog", finding.FixLabel);
    }

    [Fact(DisplayName = "Two units that both convert to the display unit — no finding")]
    public async Task TwoConvertibleUnits_NoFinding()
    {
        var stock = ProductStock.Start(Household, OnionId, Clock);
        stock.AddStock(3m, PoundId, Guid.NewGuid(), Guid.NewGuid(), Clock);
        stock.AddStock(200m, GramId, Guid.NewGuid(), Guid.NewGuid(), Clock);
        var stocks = new FakeD6StockRepository();
        stocks.Add(stock);

        var catalog = new FakeD6CatalogFacade();
        catalog.AddProduct(OnionId, "Onion Yellow", EachId, "ea");
        catalog.AddUnitCode(PoundId, "lb");
        catalog.AddUnitCode(GramId, "g");

        // Both lb and g convert to the display unit "ea" — total > 0, DisplayQuantity never falls back.
        var detector = BuildDetector(stocks, catalog, new FakeD6ConversionProvider(mismatch: false));
        var findings = await detector.DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "Single unit, unconvertible to display — DisplayQuantity falls back to the lot's own unit, not \"?\" — no D6 finding")]
    public async Task SingleUnconvertibleUnit_NoFinding()
    {
        var stock = ProductStock.Start(Household, OnionId, Clock);
        stock.AddStock(3m, PoundId, Guid.NewGuid(), Guid.NewGuid(), Clock);
        var stocks = new FakeD6StockRepository();
        stocks.Add(stock);

        var catalog = new FakeD6CatalogFacade();
        catalog.AddProduct(OnionId, "Onion Yellow", EachId, "ea");
        catalog.AddUnitCode(PoundId, "lb");

        var detector = BuildDetector(stocks, catalog, new FakeD6ConversionProvider(mismatch: true));
        var findings = await detector.DetectAsync();

        Assert.Empty(findings); // this is D1's case, not D6's
    }

    [Fact(DisplayName = "No active lots — no finding")]
    public async Task NoActiveLots_NoFinding()
    {
        var stocks = new FakeD6StockRepository();
        var catalog = new FakeD6CatalogFacade();
        catalog.AddProduct(OnionId, "Onion Yellow", EachId, "ea");

        var detector = BuildDetector(stocks, catalog, new FakeD6ConversionProvider(mismatch: true));
        var findings = await detector.DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "No tenant — returns no findings")]
    public async Task NoTenant_ReturnsEmpty()
    {
        var stock = ProductStock.Start(Household, OnionId, Clock);
        stock.AddStock(3m, PoundId, Guid.NewGuid(), Guid.NewGuid(), Clock);
        stock.AddStock(2m, GramId, Guid.NewGuid(), Guid.NewGuid(), Clock);
        var stocks = new FakeD6StockRepository();
        stocks.Add(stock);

        var catalog = new FakeD6CatalogFacade();
        catalog.AddProduct(OnionId, "Onion Yellow", EachId, "ea");
        catalog.AddUnitCode(PoundId, "lb");
        catalog.AddUnitCode(GramId, "g");

        var detector = BuildDetector(
            stocks, catalog, new FakeD6ConversionProvider(mismatch: true), new FakeD6TenantContext(null));
        var findings = await detector.DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "Fingerprint pinning: quantity change alone does NOT change the fingerprint")]
    public async Task Fingerprint_UnaffectedByQuantityChange()
    {
        var catalog = new FakeD6CatalogFacade();
        catalog.AddProduct(OnionId, "Onion Yellow", EachId, "ea");
        catalog.AddUnitCode(PoundId, "lb");
        catalog.AddUnitCode(GramId, "g");
        var conversions = new FakeD6ConversionProvider(mismatch: true);

        var stockSmall = ProductStock.Start(Household, OnionId, Clock);
        stockSmall.AddStock(1m, PoundId, Guid.NewGuid(), Guid.NewGuid(), Clock);
        stockSmall.AddStock(1m, GramId, Guid.NewGuid(), Guid.NewGuid(), Clock);
        var stocksSmall = new FakeD6StockRepository();
        stocksSmall.Add(stockSmall);
        var findingSmall = Assert.Single(await BuildDetector(stocksSmall, catalog, conversions).DetectAsync());

        var stockLarge = ProductStock.Start(Household, OnionId, Clock);
        stockLarge.AddStock(50m, PoundId, Guid.NewGuid(), Guid.NewGuid(), Clock);
        stockLarge.AddStock(80m, GramId, Guid.NewGuid(), Guid.NewGuid(), Clock);
        var stocksLarge = new FakeD6StockRepository();
        stocksLarge.Add(stockLarge);
        var findingLarge = Assert.Single(await BuildDetector(stocksLarge, catalog, conversions).DetectAsync());

        Assert.Equal(findingSmall.FactsFingerprint, findingLarge.FactsFingerprint);
    }

    [Fact(DisplayName = "Fingerprint pinning: a different unit set changes the fingerprint")]
    public async Task Fingerprint_ChangesWithDifferentUnitSet()
    {
        var eachAltId = Guid.Parse("22222222-2222-2222-2222-0000000000db");
        var catalog = new FakeD6CatalogFacade();
        catalog.AddProduct(OnionId, "Onion Yellow", EachId, "ea");
        catalog.AddUnitCode(PoundId, "lb");
        catalog.AddUnitCode(GramId, "g");
        catalog.AddUnitCode(eachAltId, "oz");
        var conversions = new FakeD6ConversionProvider(mismatch: true);

        var stockA = ProductStock.Start(Household, OnionId, Clock);
        stockA.AddStock(3m, PoundId, Guid.NewGuid(), Guid.NewGuid(), Clock);
        stockA.AddStock(2m, GramId, Guid.NewGuid(), Guid.NewGuid(), Clock);
        var stocksA = new FakeD6StockRepository();
        stocksA.Add(stockA);
        var findingA = Assert.Single(await BuildDetector(stocksA, catalog, conversions).DetectAsync());

        var stockB = ProductStock.Start(Household, OnionId, Clock);
        stockB.AddStock(3m, PoundId, Guid.NewGuid(), Guid.NewGuid(), Clock);
        stockB.AddStock(2m, eachAltId, Guid.NewGuid(), Guid.NewGuid(), Clock);
        var stocksB = new FakeD6StockRepository();
        stocksB.Add(stockB);
        var findingB = Assert.Single(await BuildDetector(stocksB, catalog, conversions).DetectAsync());

        Assert.NotEqual(findingA.FactsFingerprint, findingB.FactsFingerprint);
    }
}

// ── Test doubles ─────────────────────────────────────────────────────────────────────────────────

file sealed class FakeD6TenantContext(Guid? householdId) : ITenantContext
{
    public Guid? HouseholdId { get; } = householdId;
}

file sealed class FakeD6StockRepository : IProductStockRepository
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

file sealed class FakeD6CatalogFacade : ICatalogReadFacade
{
    private readonly List<CatalogProductInfo> _products = [];
    private readonly Dictionary<Guid, string> _unitCodes = [];

    public void AddProduct(Guid id, string name, Guid defaultUnitId, string defaultUnitCode) =>
        _products.Add(new CatalogProductInfo(id, name, null, defaultUnitId, defaultUnitCode, CanHoldStock: true));

    public void AddUnitCode(Guid unitId, string code) => _unitCodes[unitId] = code;

    public Task<CatalogProductInfo?> FindProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(_products.SingleOrDefault(p => p.Id == productId));

    public Task<IReadOnlyList<CatalogProductInfo>> ListProductsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogProductInfo>>(_products);

    public Task<IReadOnlyDictionary<Guid, string>> GetUnitCodesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(_unitCodes);

    public Task<IReadOnlyDictionary<Guid, string>> GetLocationNamesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
}

/// <summary>Converter that fails whenever from != to (simulating no cross-dimension factor anywhere), or
/// always succeeds when <c>mismatch</c> is false (every lot already converts cleanly) — same shape as D1's
/// <c>FakeD1ConversionProvider</c>.</summary>
file sealed class FakeD6ConversionProvider(bool mismatch) : IProductConversionProvider
{
    public Task<IQuantityConverter> ForProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult<IQuantityConverter>(new Converter(mismatch));

    private sealed class Converter(bool mismatch) : IQuantityConverter
    {
        public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId)
        {
            if (fromUnitId == toUnitId) return amount;
            return mismatch
                ? Result<decimal>.Failure(Error.Custom("Test.NoConversion", "No conversion factor."))
                : amount;
        }
    }
}
