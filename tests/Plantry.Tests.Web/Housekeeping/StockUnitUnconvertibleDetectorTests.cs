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
/// L2 golden tests for <see cref="StockUnitUnconvertibleDetector"/> (D1, tidy-up.md §3) — including
/// fingerprint pinning: the fingerprint covers only the unconvertible unit ids + the display unit id,
/// never quantities, so buying more of an already-unconvertible unit must not change it (an accidental
/// fingerprint change would mass-reopen every dismissed D1 finding, §4).
///
/// Tests live in Plantry.Tests.Web because the detector is in Plantry.Composition (referenced
/// transitively via Plantry.Web) — mirrors <c>ShoppingPantryReaderAdapterTests</c>' rationale.
/// </summary>
public sealed class StockUnitUnconvertibleDetectorTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly Guid HouseholdGuid = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000d1");
    private static readonly HouseholdId Household = HouseholdId.From(HouseholdGuid);

    private static readonly Guid OnionId = Guid.Parse("11111111-1111-1111-1111-0000000000d1");
    private static readonly Guid EachId = Guid.Parse("22222222-2222-2222-2222-0000000000d1");
    private static readonly Guid PoundId = Guid.Parse("22222222-2222-2222-2222-0000000000d2");

    private static ProductStock MakeStockWithLot(Guid productId, decimal quantity, Guid unitId)
    {
        var stock = ProductStock.Start(Household, productId, Clock);
        stock.AddStock(quantity, unitId, locationId: Guid.NewGuid(), userId: Guid.NewGuid(), Clock);
        return stock;
    }

    private static StockUnitUnconvertibleDetector BuildDetector(
        IProductStockRepository stocks, ICatalogReadFacade catalog, IProductConversionProvider conversions,
        ITenantContext? tenant = null) =>
        new(stocks, catalog, conversions, tenant ?? new FakeD1TenantContext(HouseholdGuid));

    [Fact(DisplayName = "Lot unit unconvertible to display unit — produces a finding naming the product and its unconvertible unit")]
    public async Task UnconvertibleLot_ProducesFinding()
    {
        var stocks = new FakeD1StockRepository();
        stocks.Add(MakeStockWithLot(OnionId, 3m, PoundId));

        var catalog = new FakeD1CatalogFacade();
        catalog.AddProduct(OnionId, "Onion Yellow", EachId, "ea");
        catalog.AddUnitCode(PoundId, "lb");

        var detector = BuildDetector(stocks, catalog, new FakeD1ConversionProvider(mismatch: true));
        var findings = await detector.DetectAsync();

        var finding = Assert.Single(findings);
        Assert.Equal(DetectorId.StockUnitUnconvertible, finding.DetectorId);
        Assert.Equal(OnionId, finding.SubjectId);
        Assert.Equal("Onion Yellow", finding.SubjectName);
        Assert.Contains("lb", finding.Specifics);
        Assert.Contains("ea", finding.Specifics);
        Assert.Equal("/Catalog/Products/" + OnionId + "#conversions", finding.FixUrl);
        // Single unconvertible unit: string must be byte-identical to pre-plantry-g223 behavior.
        Assert.Equal("3 lb in stock, display unit is ea", finding.Specifics);
    }

    [Fact(DisplayName = "Two distinct unconvertible units — specifics is a per-unit breakdown, terms ordered alphabetically by unit code")]
    public async Task TwoDistinctUnconvertibleUnits_ProducesPerUnitBreakdown()
    {
        var gramId = Guid.Parse("22222222-2222-2222-2222-0000000000d3");
        var stocks = new FakeD1StockRepository();
        var stock = ProductStock.Start(Household, OnionId, Clock);
        stock.AddStock(3m, PoundId, locationId: Guid.NewGuid(), userId: Guid.NewGuid(), Clock);
        stock.AddStock(200m, gramId, locationId: Guid.NewGuid(), userId: Guid.NewGuid(), Clock);
        stocks.Add(stock);

        var catalog = new FakeD1CatalogFacade();
        catalog.AddProduct(OnionId, "Onion Yellow", EachId, "ea");
        catalog.AddUnitCode(PoundId, "lb");
        catalog.AddUnitCode(gramId, "g");

        var detector = BuildDetector(stocks, catalog, new FakeD1ConversionProvider(mismatch: true));
        var finding = Assert.Single(await detector.DetectAsync());

        Assert.Equal("200 g + 3 lb in stock, display unit is ea", finding.Specifics);
    }

    [Fact(DisplayName = "Multiple lots of the same unconvertible unit — quantities sum into a single term")]
    public async Task SameUnconvertibleUnit_MultipleLots_SumIntoOneTerm()
    {
        var stocks = new FakeD1StockRepository();
        var stock = ProductStock.Start(Household, OnionId, Clock);
        stock.AddStock(3m, PoundId, locationId: Guid.NewGuid(), userId: Guid.NewGuid(), Clock);
        stock.AddStock(2m, PoundId, locationId: Guid.NewGuid(), userId: Guid.NewGuid(), Clock);
        stocks.Add(stock);

        var catalog = new FakeD1CatalogFacade();
        catalog.AddProduct(OnionId, "Onion Yellow", EachId, "ea");
        catalog.AddUnitCode(PoundId, "lb");

        var detector = BuildDetector(stocks, catalog, new FakeD1ConversionProvider(mismatch: true));
        var finding = Assert.Single(await detector.DetectAsync());

        Assert.Equal("5 lb in stock, display unit is ea", finding.Specifics);
    }

    [Fact(DisplayName = "All lots convert cleanly — no finding")]
    public async Task AllLotsConvertible_NoFinding()
    {
        var stocks = new FakeD1StockRepository();
        stocks.Add(MakeStockWithLot(OnionId, 3m, EachId));

        var catalog = new FakeD1CatalogFacade();
        catalog.AddProduct(OnionId, "Onion Yellow", EachId, "ea");

        var detector = BuildDetector(stocks, catalog, new FakeD1ConversionProvider(mismatch: false));
        var findings = await detector.DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "No tenant — returns no findings without inspecting stock")]
    public async Task NoTenant_ReturnsEmpty()
    {
        var stocks = new FakeD1StockRepository();
        stocks.Add(MakeStockWithLot(OnionId, 3m, PoundId));
        var catalog = new FakeD1CatalogFacade();
        catalog.AddProduct(OnionId, "Onion Yellow", EachId, "ea");

        var detector = BuildDetector(
            stocks, catalog, new FakeD1ConversionProvider(mismatch: true), new FakeD1TenantContext(null));
        var findings = await detector.DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "Fingerprint pinning: quantity change alone does NOT change the fingerprint")]
    public async Task Fingerprint_UnaffectedByQuantityChange()
    {
        var catalog = new FakeD1CatalogFacade();
        catalog.AddProduct(OnionId, "Onion Yellow", EachId, "ea");
        catalog.AddUnitCode(PoundId, "lb");
        var conversions = new FakeD1ConversionProvider(mismatch: true);

        var stocksSmall = new FakeD1StockRepository();
        stocksSmall.Add(MakeStockWithLot(OnionId, 1m, PoundId));
        var findingSmall = Assert.Single(await BuildDetector(stocksSmall, catalog, conversions).DetectAsync());

        var stocksLarge = new FakeD1StockRepository();
        stocksLarge.Add(MakeStockWithLot(OnionId, 50m, PoundId));
        var findingLarge = Assert.Single(await BuildDetector(stocksLarge, catalog, conversions).DetectAsync());

        Assert.Equal(findingSmall.FactsFingerprint, findingLarge.FactsFingerprint);
    }

    [Fact(DisplayName = "Fingerprint pinning: a different unconvertible unit set changes the fingerprint")]
    public async Task Fingerprint_ChangesWithDifferentUnconvertibleUnit()
    {
        var gramId = Guid.Parse("22222222-2222-2222-2222-0000000000d3");
        var catalog = new FakeD1CatalogFacade();
        catalog.AddProduct(OnionId, "Onion Yellow", EachId, "ea");
        catalog.AddUnitCode(PoundId, "lb");
        catalog.AddUnitCode(gramId, "g");
        var conversions = new FakeD1ConversionProvider(mismatch: true);

        var stocksLb = new FakeD1StockRepository();
        stocksLb.Add(MakeStockWithLot(OnionId, 3m, PoundId));
        var findingLb = Assert.Single(await BuildDetector(stocksLb, catalog, conversions).DetectAsync());

        var stocksG = new FakeD1StockRepository();
        stocksG.Add(MakeStockWithLot(OnionId, 3m, gramId));
        var findingG = Assert.Single(await BuildDetector(stocksG, catalog, conversions).DetectAsync());

        Assert.NotEqual(findingLb.FactsFingerprint, findingG.FactsFingerprint);
    }
}

// ── Test doubles ─────────────────────────────────────────────────────────────────────────────────

file sealed class FakeD1TenantContext(Guid? householdId) : ITenantContext
{
    public Guid? HouseholdId { get; } = householdId;
}

file sealed class FakeD1StockRepository : IProductStockRepository
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

file sealed class FakeD1CatalogFacade : ICatalogReadFacade
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

/// <summary>Converter that fails whenever from != to (simulating no cross-dimension factor), or always
/// succeeds when <c>mismatch</c> is false (every lot already matches the product's default unit).</summary>
file sealed class FakeD1ConversionProvider(bool mismatch) : IProductConversionProvider
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
