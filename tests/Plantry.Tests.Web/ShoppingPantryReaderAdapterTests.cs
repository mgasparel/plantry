using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Shopping.Application;
using Plantry.Tests.Web.Infrastructure;
using Plantry.Web.Shopping;

namespace Plantry.Tests.Web;

/// <summary>
/// L2 unit tests for <see cref="ShoppingPantryReaderAdapter"/> — the Shopping→Inventory
/// anti-corruption read port (plantry-juh). Verifies on-hand quantity aggregation, IsLow
/// derivation, tenant scoping, and that the adapter stays behind the port boundary
/// (Shopping never sees raw Inventory domain types).
///
/// Tests live in Plantry.Tests.Web because the adapter is in Plantry.Web; they do NOT use
/// WebApplicationFactory — they instantiate the adapter directly with in-memory fakes.
/// </summary>
public sealed class ShoppingPantryReaderAdapterTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly Guid HouseholdGuid = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly HouseholdId Household = HouseholdId.From(HouseholdGuid);

    private static readonly Guid MilkId  = Guid.Parse("11111111-1111-1111-1111-000000000001");
    private static readonly Guid FlourId = Guid.Parse("11111111-1111-1111-1111-000000000002");
    private static readonly Guid LitreId = Guid.Parse("22222222-2222-2222-2222-000000000001");
    private static readonly Guid GramId  = Guid.Parse("22222222-2222-2222-2222-000000000002");

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ShoppingPantryReaderAdapter BuildAdapter(
        IProductStockRepository stocks,
        ICatalogReadFacade catalog,
        ITenantContext? tenantCtx = null)
    {
        var tenant = tenantCtx ?? new FakePantryTenantContext(HouseholdGuid);
        return new ShoppingPantryReaderAdapter(stocks, catalog, new FakePantryConversionProvider(), tenant);
    }

    private static ProductStock MakeStock(Guid productId) =>
        ProductStock.Start(Household, productId, Clock);

    private static ProductStock MakeStockWithLot(Guid productId, decimal quantity, Guid unitId)
    {
        var stock = ProductStock.Start(Household, productId, Clock);
        stock.AddStock(quantity, unitId, locationId: Guid.NewGuid(), userId: Guid.NewGuid(), Clock);
        return stock;
    }

    /// <summary>Stock with an active lot and a low stock threshold set (for running-low tests).</summary>
    private static ProductStock MakeStockWithLotAndThreshold(Guid productId, decimal quantity, Guid unitId, decimal threshold)
    {
        var stock = MakeStockWithLot(productId, quantity, unitId);
        stock.SetLowStockThreshold(threshold, Clock);
        return stock;
    }

    /// <summary>Stock with a threshold set but no active lots (out, but with a threshold configured).</summary>
    private static ProductStock MakeStockWithThresholdNoLots(Guid productId, decimal threshold)
    {
        var stock = ProductStock.Start(Household, productId, Clock);
        stock.SetLowStockThreshold(threshold, Clock);
        return stock;
    }

    // ── Core enrichment ───────────────────────────────────────────────────────

    [Fact(DisplayName = "GetStockLevels — product with active lot: OnHand = lot quantity, IsLow = false")]
    public async Task GetStockLevels_ProductWithLot_OnHandPopulated()
    {
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithLot(MilkId, 2m, LitreId));

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L");

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetStockLevelsAsync([MilkId]);

        var level = Assert.Single(result).Value;
        Assert.Equal(MilkId, level.ProductId);
        Assert.Equal(2m, level.OnHand);
        Assert.Equal("L", level.UnitCode);
        Assert.False(level.IsLow);
    }

    [Fact(DisplayName = "GetStockLevels — out product, no threshold: OnHand = 0, IsLow = false (out is not running low)")]
    public async Task GetStockLevels_ProductWithNoLots_OnHandZeroIsLowFalse()
    {
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStock(MilkId)); // no lots, no threshold

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L");

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetStockLevelsAsync([MilkId]);

        var level = Assert.Single(result).Value;
        Assert.Equal(0m, level.OnHand);
        // IsLow is running-low only (0 < onHand ≤ threshold). Out (onHand = 0) is NOT running low.
        Assert.False(level.IsLow);
    }

    // ── Tri-state IsLow computation (plantry-43y): in-stock / running-low / out ─

    [Fact(DisplayName = "GetStockLevels — in-stock above threshold: IsLow = false")]
    public async Task GetStockLevels_AboveThreshold_IsLowFalse()
    {
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithLotAndThreshold(MilkId, quantity: 10m, LitreId, threshold: 3m));

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L");

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetStockLevelsAsync([MilkId]);

        var level = Assert.Single(result).Value;
        Assert.Equal(10m, level.OnHand);
        Assert.False(level.IsLow);
    }

    [Fact(DisplayName = "GetStockLevels — running low (0 < onHand < threshold): IsLow = true")]
    public async Task GetStockLevels_BelowThreshold_IsLowTrue()
    {
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithLotAndThreshold(MilkId, quantity: 2m, LitreId, threshold: 3m));

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L");

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetStockLevelsAsync([MilkId]);

        var level = Assert.Single(result).Value;
        Assert.Equal(2m, level.OnHand);
        Assert.True(level.IsLow);
    }

    [Fact(DisplayName = "GetStockLevels — onHand exactly at threshold: IsLow = true (inclusive boundary)")]
    public async Task GetStockLevels_AtThreshold_IsLowTrue()
    {
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithLotAndThreshold(MilkId, quantity: 3m, LitreId, threshold: 3m));

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L");

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetStockLevelsAsync([MilkId]);

        var level = Assert.Single(result).Value;
        Assert.Equal(3m, level.OnHand);
        Assert.True(level.IsLow);
    }

    [Fact(DisplayName = "GetStockLevels — out with threshold set: OnHand = 0, IsLow = false (out beats low)")]
    public async Task GetStockLevels_OutWithThreshold_IsLowFalse()
    {
        var stocks = new FakePantryStockRepository();
        // Threshold set but no active lots: IsRunningLow(0) alone would be true (0 ≤ 3); the adapter's
        // onHand > 0 guard forces IsLow = false so out and low never both fire.
        stocks.Add(MakeStockWithThresholdNoLots(MilkId, threshold: 3m));

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L");

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetStockLevelsAsync([MilkId]);

        var level = Assert.Single(result).Value;
        Assert.Equal(0m, level.OnHand);
        Assert.False(level.IsLow);
    }

    // ── Restock-candidate predicate (plantry-43y): GetLowStockProductsAsync = low ∪ out ─

    [Fact(DisplayName = "GetLowStockProducts — includes running-low, out, and excludes in-stock")]
    public async Task GetLowStockProducts_ReturnsLowAndOut_ExcludesInStock()
    {
        var eggsId = Guid.Parse("11111111-1111-1111-1111-000000000003");

        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithLotAndThreshold(MilkId, quantity: 2m, LitreId, threshold: 3m));   // running low
        stocks.Add(MakeStockWithThresholdNoLots(FlourId, threshold: 500m));                        // out
        stocks.Add(MakeStockWithLotAndThreshold(eggsId, quantity: 10m, LitreId, threshold: 3m));   // in-stock

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L");
        catalog.AddProduct(FlourId, defaultUnitId: GramId, defaultUnitCode: "g");
        catalog.AddProduct(eggsId, defaultUnitId: LitreId, defaultUnitCode: "L");

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetLowStockProductsAsync();

        Assert.Equal(2, result.Count);
        var milk = Assert.Single(result, l => l.ProductId == MilkId);
        Assert.True(milk.IsLow);   // running low
        var flour = Assert.Single(result, l => l.ProductId == FlourId);
        Assert.False(flour.IsLow); // out — surfaced via OnHand ≤ 0, not IsLow
        Assert.Equal(0m, flour.OnHand);
        Assert.DoesNotContain(result, l => l.ProductId == eggsId); // in-stock excluded
    }

    [Fact(DisplayName = "GetStockLevels — product with multiple lots: OnHand is the sum")]
    public async Task GetStockLevels_MultipleLots_OnHandIsSumOfActiveLots()
    {
        var stocks = new FakePantryStockRepository();
        var stock = ProductStock.Start(Household, MilkId, Clock);
        stock.AddStock(1m, LitreId, locationId: Guid.NewGuid(), userId: Guid.NewGuid(), Clock);
        stock.AddStock(2m, LitreId, locationId: Guid.NewGuid(), userId: Guid.NewGuid(), Clock);
        stocks.Add(stock);

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L");

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetStockLevelsAsync([MilkId]);

        Assert.Equal(3m, result[MilkId].OnHand);
    }

    [Fact(DisplayName = "GetStockLevels — product not stocked at all: omitted from result")]
    public async Task GetStockLevels_ProductNeverStocked_OmittedFromResult()
    {
        var stocks = new FakePantryStockRepository(); // empty
        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L");

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetStockLevelsAsync([MilkId]);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "GetStockLevels — multiple products: each resolved independently")]
    public async Task GetStockLevels_MultipleProducts_EachResolvedIndependently()
    {
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithLot(MilkId, 1m, LitreId));
        stocks.Add(MakeStockWithLot(FlourId, 500m, GramId));

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L");
        catalog.AddProduct(FlourId, defaultUnitId: GramId, defaultUnitCode: "g");

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetStockLevelsAsync([MilkId, FlourId]);

        Assert.Equal(2, result.Count);
        Assert.Equal(1m, result[MilkId].OnHand);
        Assert.Equal("L", result[MilkId].UnitCode);
        Assert.Equal(500m, result[FlourId].OnHand);
        Assert.Equal("g", result[FlourId].UnitCode);
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact(DisplayName = "GetStockLevels — empty product list: returns empty dictionary without calling repository")]
    public async Task GetStockLevels_EmptyProductList_ReturnsEmpty()
    {
        var stocks = new FakePantryStockRepository();
        var catalog = new FakePantryCatalogFacade();
        var adapter = BuildAdapter(stocks, catalog);

        var result = await adapter.GetStockLevelsAsync([]);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "GetStockLevels — no tenant context: returns empty dictionary (no household to query)")]
    public async Task GetStockLevels_NoTenant_ReturnsEmpty()
    {
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithLot(MilkId, 1m, LitreId));

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L");

        var adapter = BuildAdapter(stocks, catalog, new FakePantryTenantContext(null));
        var result = await adapter.GetStockLevelsAsync([MilkId]);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "GetStockLevels — product archived from catalog: omitted from result")]
    public async Task GetStockLevels_ProductNotInCatalog_OmittedFromResult()
    {
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithLot(MilkId, 1m, LitreId));

        // Catalog has no entry for MilkId — product was archived.
        var catalog = new FakePantryCatalogFacade();

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetStockLevelsAsync([MilkId]);

        Assert.Empty(result);
    }
}

// ── Test doubles ─────────────────────────────────────────────────────────────────────────────────

file sealed class FakePantryTenantContext(Guid? householdId) : ITenantContext
{
    public Guid? HouseholdId { get; } = householdId;
}

file sealed class FakePantryStockRepository : IProductStockRepository
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

file sealed class FakePantryCatalogFacade : ICatalogReadFacade
{
    private readonly List<CatalogProductInfo> _products = [];

    public void AddProduct(Guid id, Guid defaultUnitId, string defaultUnitCode) =>
        _products.Add(new CatalogProductInfo(id, "Product", null, defaultUnitId, defaultUnitCode, CanHoldStock: true));

    public Task<CatalogProductInfo?> FindProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(_products.SingleOrDefault(p => p.Id == productId));

    public Task<IReadOnlyList<CatalogProductInfo>> ListProductsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogProductInfo>>(_products);

    public Task<IReadOnlyDictionary<Guid, string>> GetUnitCodesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

    public Task<IReadOnlyDictionary<Guid, string>> GetLocationNamesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
}

/// <summary>Identity converter: same-unit and cross-unit both pass through (tests use matching units).</summary>
file sealed class FakePantryConversionProvider : IProductConversionProvider
{
    public Task<IQuantityConverter> ForProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult<IQuantityConverter>(new IdentityConverter());

    private sealed class IdentityConverter : IQuantityConverter
    {
        public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId) => amount;
    }
}
