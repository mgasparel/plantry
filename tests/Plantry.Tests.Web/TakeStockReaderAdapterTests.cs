using Plantry.Catalog.Domain;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Inventory;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Web;

/// <summary>
/// L2 unit tests for <see cref="TakeStockReaderAdapter"/> — the Take Stock read port (P4-3 / TS-10).
/// Verifies all five methods: ListLocations, ListLocationRows (C5 union), ListNoLocationRows (J7),
/// ListLots, and SearchProducts, using in-memory fakes.
///
/// Tests live in Plantry.Tests.Web because the adapter is in Plantry.Web; they do NOT use
/// WebApplicationFactory — they instantiate the adapter directly with in-memory fakes.
/// </summary>
public sealed class TakeStockReaderAdapterTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly Guid HouseholdGuid = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000099");
    private static readonly HouseholdId Household = HouseholdId.From(HouseholdGuid);

    private static readonly Guid PantryLocId = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid FridgeLocId  = Guid.Parse("11111111-0000-0000-0000-000000000002");

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static TakeStockReaderAdapter BuildAdapter(
        IProductStockRepository stocks,
        IProductRepository prods,
        IUnitRepository unitRepo,
        ILocationRepository locs,
        Guid? householdOverride = null)
    {
        var tenant = new TsTenantContext(householdOverride ?? HouseholdGuid);
        return new TakeStockReaderAdapter(stocks, prods, unitRepo, locs, new TsPassThroughConversions(), tenant);
    }

    /// <summary>Creates a unit and registers it in the repo; returns both the unit and its Id.</summary>
    private static (CatalogUnit Unit, Guid Id) MakeUnit(IUnitRepository repo, string code)
    {
        var unit = CatalogUnit.Create(Household, code, code, Dimension.Mass, 1m, isBase: true);
        repo.AddAsync(unit).GetAwaiter().GetResult();
        return (unit, unit.Id.Value);
    }

    // ── ListLocations ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "ListLocations — returns active locations ordered by name")]
    public async Task ListLocations_ReturnsActiveLocationsOrderedByName()
    {
        var locs = new TsLocationRepository();
        locs.Add(Location.Create(Household, "Zeta", LocationType.Ambient));
        locs.Add(Location.Create(Household, "Alpha", LocationType.Ambient));

        var adapter = BuildAdapter(new TsStockRepository(), new TsProductRepository(), new TsUnitRepository(), locs);
        var result = await adapter.ListLocationsAsync();

        Assert.Equal(2, result.Count);
        Assert.Equal("Alpha", result[0].LocationName);
        Assert.Equal("Zeta",  result[1].LocationName);
    }

    [Fact(DisplayName = "ListLocations — archived locations are excluded")]
    public async Task ListLocations_ArchivedLocationsExcluded()
    {
        var locs = new TsLocationRepository();
        locs.Add(Location.Create(Household, "Pantry", LocationType.Ambient));
        var archived = Location.Create(Household, "OldFridge", LocationType.Frozen);
        archived.Archive(Clock);
        locs.Add(archived);

        var adapter = BuildAdapter(new TsStockRepository(), new TsProductRepository(), new TsUnitRepository(), locs);
        var result = await adapter.ListLocationsAsync();

        var row = Assert.Single(result);
        Assert.Equal("Pantry", row.LocationName);
    }

    // ── ListLocationRows (C5 union) ───────────────────────────────────────────

    [Fact(DisplayName = "ListLocationRows branch A — product with active stock in location")]
    public async Task ListLocationRows_BranchA_ProductWithActiveStock()
    {
        var units = new TsUnitRepository();
        var (_, gramId) = MakeUnit(units, "g");

        var prods = new TsProductRepository();
        var flour = Product.Create(Household, "Flour", UnitId.From(gramId), Clock);
        prods.Add(flour);

        var stocks = new TsStockRepository();
        var stock = ProductStock.Start(Household, flour.Id.Value, Clock);
        stock.AddStock(500m, gramId, PantryLocId, userId: Guid.NewGuid(), Clock);
        stocks.Add(stock);

        var adapter = BuildAdapter(stocks, prods, units, new TsLocationRepository());
        var result = await adapter.ListLocationRowsAsync(PantryLocId);

        var row = Assert.Single(result);
        Assert.Equal(flour.Id.Value, row.ProductId);
        Assert.Equal("Flour", row.ProductName);
        Assert.Equal(500m, row.RecordedQuantity);
        Assert.True(row.HasActiveStock);
        Assert.Equal("g", row.DisplayUnitCode);
    }

    [Fact(DisplayName = "ListLocationRows branch B — product with default location but no active stock here")]
    public async Task ListLocationRows_BranchB_DefaultLocationProductNoStock()
    {
        var units = new TsUnitRepository();
        var (_, gramId) = MakeUnit(units, "g");

        var prods = new TsProductRepository();
        var flour = Product.Create(Household, "Flour", UnitId.From(gramId), Clock);
        flour.SetDefaultLocation(LocationId.From(PantryLocId), Clock);
        prods.Add(flour);

        // No stock anywhere.
        var adapter = BuildAdapter(new TsStockRepository(), prods, units, new TsLocationRepository());
        var result = await adapter.ListLocationRowsAsync(PantryLocId);

        var row = Assert.Single(result);
        Assert.Equal(flour.Id.Value, row.ProductId);
        Assert.Equal(0m, row.RecordedQuantity);
        Assert.False(row.HasActiveStock);
    }

    [Fact(DisplayName = "ListLocationRows — branch A wins when product satisfies both branches")]
    public async Task ListLocationRows_BranchAWinsOverBranchB()
    {
        var units = new TsUnitRepository();
        var (_, gramId) = MakeUnit(units, "g");

        var prods = new TsProductRepository();
        var flour = Product.Create(Household, "Flour", UnitId.From(gramId), Clock);
        flour.SetDefaultLocation(LocationId.From(PantryLocId), Clock); // also a branch B candidate
        prods.Add(flour);

        var stocks = new TsStockRepository();
        var stock = ProductStock.Start(Household, flour.Id.Value, Clock);
        stock.AddStock(300m, gramId, PantryLocId, userId: Guid.NewGuid(), Clock);
        stocks.Add(stock);

        var adapter = BuildAdapter(stocks, prods, units, new TsLocationRepository());
        var result = await adapter.ListLocationRowsAsync(PantryLocId);

        var row = Assert.Single(result); // only one row, not two
        Assert.True(row.HasActiveStock);
        Assert.Equal(300m, row.RecordedQuantity);
    }

    [Fact(DisplayName = "ListLocationRows — product with stock in a different location is excluded")]
    public async Task ListLocationRows_StockInDifferentLocation_Excluded()
    {
        var units = new TsUnitRepository();
        var (_, gramId) = MakeUnit(units, "g");

        var prods = new TsProductRepository();
        var flour = Product.Create(Household, "Flour", UnitId.From(gramId), Clock);
        // No default location
        prods.Add(flour);

        var stocks = new TsStockRepository();
        var stock = ProductStock.Start(Household, flour.Id.Value, Clock);
        stock.AddStock(500m, gramId, FridgeLocId, userId: Guid.NewGuid(), Clock); // not PantryLocId
        stocks.Add(stock);

        var adapter = BuildAdapter(stocks, prods, units, new TsLocationRepository());
        var result = await adapter.ListLocationRowsAsync(PantryLocId);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "ListLocationRows — aggregates multiple active lots for the same product")]
    public async Task ListLocationRows_AggregatesMultipleLotsForProduct()
    {
        var units = new TsUnitRepository();
        var (_, gramId) = MakeUnit(units, "g");

        var prods = new TsProductRepository();
        var flour = Product.Create(Household, "Flour", UnitId.From(gramId), Clock);
        prods.Add(flour);

        var stocks = new TsStockRepository();
        var stock = ProductStock.Start(Household, flour.Id.Value, Clock);
        stock.AddStock(200m, gramId, PantryLocId, userId: Guid.NewGuid(), Clock);
        stock.AddStock(300m, gramId, PantryLocId, userId: Guid.NewGuid(), Clock);
        stocks.Add(stock);

        var adapter = BuildAdapter(stocks, prods, units, new TsLocationRepository());
        var result = await adapter.ListLocationRowsAsync(PantryLocId);

        var row = Assert.Single(result);
        Assert.Equal(500m, row.RecordedQuantity);
    }

    [Fact(DisplayName = "ListLocationRows — no tenant context returns empty")]
    public async Task ListLocationRows_NoTenant_ReturnsEmpty()
    {
        var adapter = new TakeStockReaderAdapter(
            new TsStockRepository(), new TsProductRepository(), new TsUnitRepository(),
            new TsLocationRepository(), new TsPassThroughConversions(),
            new TsTenantContext(null));

        var result = await adapter.ListLocationRowsAsync(PantryLocId);

        Assert.Empty(result);
    }

    // ── ListNoLocationRows (J7) ───────────────────────────────────────────────

    [Fact(DisplayName = "ListNoLocationRows — tracked product with active stock and no default location returned")]
    public async Task ListNoLocationRows_TrackedProductNoDefaultLocation_Returned()
    {
        var units = new TsUnitRepository();
        var (_, gramId) = MakeUnit(units, "g");

        var prods = new TsProductRepository();
        var flour = Product.Create(Household, "Flour", UnitId.From(gramId), Clock);
        // No default location
        prods.Add(flour);

        var stocks = new TsStockRepository();
        var stock = ProductStock.Start(Household, flour.Id.Value, Clock);
        stock.AddStock(500m, gramId, PantryLocId, userId: Guid.NewGuid(), Clock);
        stocks.Add(stock);

        var adapter = BuildAdapter(stocks, prods, units, new TsLocationRepository());
        var result = await adapter.ListNoLocationRowsAsync();

        var row = Assert.Single(result);
        Assert.Equal(flour.Id.Value, row.ProductId);
        Assert.Equal(500m, row.RecordedQuantity);
    }

    [Fact(DisplayName = "ListNoLocationRows — product with a default location assigned is excluded")]
    public async Task ListNoLocationRows_ProductWithDefaultLocation_Excluded()
    {
        var units = new TsUnitRepository();
        var (_, gramId) = MakeUnit(units, "g");

        var prods = new TsProductRepository();
        var flour = Product.Create(Household, "Flour", UnitId.From(gramId), Clock);
        flour.SetDefaultLocation(LocationId.From(PantryLocId), Clock); // has a home
        prods.Add(flour);

        var stocks = new TsStockRepository();
        var stock = ProductStock.Start(Household, flour.Id.Value, Clock);
        stock.AddStock(500m, gramId, PantryLocId, userId: Guid.NewGuid(), Clock);
        stocks.Add(stock);

        var adapter = BuildAdapter(stocks, prods, units, new TsLocationRepository());
        var result = await adapter.ListNoLocationRowsAsync();

        Assert.Empty(result);
    }

    [Fact(DisplayName = "ListNoLocationRows — product with no active stock is excluded")]
    public async Task ListNoLocationRows_NoActiveLots_Excluded()
    {
        var units = new TsUnitRepository();
        var (_, gramId) = MakeUnit(units, "g");

        var prods = new TsProductRepository();
        var flour = Product.Create(Household, "Flour", UnitId.From(gramId), Clock);
        prods.Add(flour);

        // No stock rows at all
        var adapter = BuildAdapter(new TsStockRepository(), prods, units, new TsLocationRepository());
        var result = await adapter.ListNoLocationRowsAsync();

        Assert.Empty(result);
    }

    [Fact(DisplayName = "ListNoLocationRows — recorded quantity sums across all physical locations")]
    public async Task ListNoLocationRows_SumsQuantityAcrossAllLocations()
    {
        var units = new TsUnitRepository();
        var (_, gramId) = MakeUnit(units, "g");

        var prods = new TsProductRepository();
        var flour = Product.Create(Household, "Flour", UnitId.From(gramId), Clock);
        prods.Add(flour);

        var stocks = new TsStockRepository();
        var stock = ProductStock.Start(Household, flour.Id.Value, Clock);
        stock.AddStock(200m, gramId, PantryLocId, userId: Guid.NewGuid(), Clock);
        stock.AddStock(300m, gramId, FridgeLocId, userId: Guid.NewGuid(), Clock);
        stocks.Add(stock);

        var adapter = BuildAdapter(stocks, prods, units, new TsLocationRepository());
        var result = await adapter.ListNoLocationRowsAsync();

        var row = Assert.Single(result);
        Assert.Equal(500m, row.RecordedQuantity);
    }

    [Fact(DisplayName = "ListNoLocationRows — no tenant context returns empty")]
    public async Task ListNoLocationRows_NoTenant_ReturnsEmpty()
    {
        var adapter = new TakeStockReaderAdapter(
            new TsStockRepository(), new TsProductRepository(), new TsUnitRepository(),
            new TsLocationRepository(), new TsPassThroughConversions(),
            new TsTenantContext(null));

        var result = await adapter.ListNoLocationRowsAsync();

        Assert.Empty(result);
    }

    // ── ListLots ──────────────────────────────────────────────────────────────

    [Fact(DisplayName = "ListLots — returns active lots for (product, location)")]
    public async Task ListLots_ReturnsActiveLotsForProductAndLocation()
    {
        var units = new TsUnitRepository();
        var (_, gramId) = MakeUnit(units, "g");

        var productId = Guid.CreateVersion7();

        var stocks = new TsStockRepository();
        var stock = ProductStock.Start(Household, productId, Clock);
        stock.AddStock(500m, gramId, PantryLocId, userId: Guid.NewGuid(), Clock,
            expiryDate: new DateOnly(2027, 1, 1));
        stock.AddStock(200m, gramId, FridgeLocId, userId: Guid.NewGuid(), Clock); // different location
        stocks.Add(stock);

        var adapter = BuildAdapter(stocks, new TsProductRepository(), units, new TsLocationRepository());
        var result = await adapter.ListLotsAsync(productId, PantryLocId);

        var lot = Assert.Single(result);
        Assert.Equal(500m, lot.Quantity);
        Assert.Equal("g", lot.UnitCode);
        Assert.Equal(new DateOnly(2027, 1, 1), lot.ExpiryDate);
    }

    [Fact(DisplayName = "ListLots — no stock for product returns empty")]
    public async Task ListLots_NoStock_ReturnsEmpty()
    {
        var adapter = BuildAdapter(new TsStockRepository(), new TsProductRepository(),
            new TsUnitRepository(), new TsLocationRepository());

        var result = await adapter.ListLotsAsync(Guid.CreateVersion7(), PantryLocId);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "ListLots — depleted lots are excluded")]
    public async Task ListLots_DepletedLots_Excluded()
    {
        var units = new TsUnitRepository();
        var (_, gramId) = MakeUnit(units, "g");

        var productId = Guid.CreateVersion7();

        var stocks = new TsStockRepository();
        var stock = ProductStock.Start(Household, productId, Clock);
        stock.AddStock(100m, gramId, PantryLocId, userId: Guid.NewGuid(), Clock);
        stock.Consume(100m, gramId, StockReason.Consumed, new TsIdentityConverter(),
            userId: Guid.NewGuid(), Clock); // depletes the lot
        stocks.Add(stock);

        var adapter = BuildAdapter(stocks, new TsProductRepository(), units, new TsLocationRepository());
        var result = await adapter.ListLotsAsync(productId, PantryLocId);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "ListLots — no tenant context returns empty")]
    public async Task ListLots_NoTenant_ReturnsEmpty()
    {
        var adapter = new TakeStockReaderAdapter(
            new TsStockRepository(), new TsProductRepository(), new TsUnitRepository(),
            new TsLocationRepository(), new TsPassThroughConversions(),
            new TsTenantContext(null));

        var result = await adapter.ListLotsAsync(Guid.CreateVersion7(), PantryLocId);

        Assert.Empty(result);
    }

    // ── SearchProducts ────────────────────────────────────────────────────────

    [Fact(DisplayName = "SearchProducts — exact match comes before contains match")]
    public async Task SearchProducts_ExactBeforeContains()
    {
        var units = new TsUnitRepository();
        var (_, gramId) = MakeUnit(units, "g");

        var prods = new TsProductRepository();
        prods.Add(Product.Create(Household, "Flour", UnitId.From(gramId), Clock));
        prods.Add(Product.Create(Household, "Wholemeal Flour", UnitId.From(gramId), Clock));
        prods.Add(Product.Create(Household, "Self Raising Flour", UnitId.From(gramId), Clock));

        var adapter = BuildAdapter(new TsStockRepository(), prods, units, new TsLocationRepository());
        var result = await adapter.SearchProductsAsync("Flour");

        Assert.Equal(3, result.Count);
        Assert.Equal("Flour", result[0].Name); // exact first
        Assert.Equal("Self Raising Flour", result[1].Name); // then alphabetical
        Assert.Equal("Wholemeal Flour", result[2].Name);
    }

    [Fact(DisplayName = "SearchProducts — case-insensitive contains match")]
    public async Task SearchProducts_CaseInsensitiveContains()
    {
        var units = new TsUnitRepository();
        var (_, gramId) = MakeUnit(units, "g");

        var prods = new TsProductRepository();
        prods.Add(Product.Create(Household, "Whole Wheat Flour", UnitId.From(gramId), Clock));

        var adapter = BuildAdapter(new TsStockRepository(), prods, units, new TsLocationRepository());
        var result = await adapter.SearchProductsAsync("flour");

        Assert.Single(result);
        Assert.Equal("Whole Wheat Flour", result[0].Name);
    }

    [Fact(DisplayName = "SearchProducts — no match returns empty")]
    public async Task SearchProducts_NoMatch_ReturnsEmpty()
    {
        var units = new TsUnitRepository();
        var (_, gramId) = MakeUnit(units, "g");

        var prods = new TsProductRepository();
        prods.Add(Product.Create(Household, "Flour", UnitId.From(gramId), Clock));

        var adapter = BuildAdapter(new TsStockRepository(), prods, units, new TsLocationRepository());
        var result = await adapter.SearchProductsAsync("milk");

        Assert.Empty(result);
    }

    [Fact(DisplayName = "SearchProducts — blank query returns empty")]
    public async Task SearchProducts_BlankQuery_ReturnsEmpty()
    {
        var units = new TsUnitRepository();
        var (_, gramId) = MakeUnit(units, "g");

        var prods = new TsProductRepository();
        prods.Add(Product.Create(Household, "Flour", UnitId.From(gramId), Clock));

        var adapter = BuildAdapter(new TsStockRepository(), prods, units, new TsLocationRepository());
        var result = await adapter.SearchProductsAsync("   ");

        Assert.Empty(result);
    }

    [Fact(DisplayName = "SearchProducts — result carries DefaultUnitId (path A unit pre-fill)")]
    public async Task SearchProducts_ResultCarriesDefaultUnitId()
    {
        var units = new TsUnitRepository();
        var (_, gramId) = MakeUnit(units, "g");

        var prods = new TsProductRepository();
        prods.Add(Product.Create(Household, "Flour", UnitId.From(gramId), Clock));

        var adapter = BuildAdapter(new TsStockRepository(), prods, units, new TsLocationRepository());
        var result = await adapter.SearchProductsAsync("Flour");

        var match = Assert.Single(result);
        Assert.Equal(gramId, match.DefaultUnitId);
    }

    [Fact(DisplayName = "SearchProducts — parent products (HasVariants) are excluded")]
    public async Task SearchProducts_ParentProducts_Excluded()
    {
        var units = new TsUnitRepository();
        var (_, gramId) = MakeUnit(units, "g");

        var prods = new TsProductRepository();
        var parent = Product.Create(Household, "Flour", UnitId.From(gramId), Clock);
        var variant = Product.Create(Household, "Wholemeal Flour", UnitId.From(gramId), Clock);
        variant.MakeVariantOf(parent.Id, Clock);
        parent.SetHasVariants(true, Clock); // parent cannot hold stock
        prods.Add(parent);
        prods.Add(variant);

        var adapter = BuildAdapter(new TsStockRepository(), prods, units, new TsLocationRepository());
        var result = await adapter.SearchProductsAsync("Flour");

        // Only variant — parent is excluded (CanHoldStock = false)
        var row = Assert.Single(result);
        Assert.Equal("Wholemeal Flour", row.Name);
    }
}

// ── Test doubles ──────────────────────────────────────────────────────────────

file sealed class TsTenantContext(Guid? householdId) : ITenantContext
{
    public Guid? HouseholdId { get; } = householdId;
}

file sealed class TsStockRepository : IProductStockRepository
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

    public Task<bool> TryAddAndSaveAsync(ProductStock stock, CancellationToken ct = default)
    {
        _stocks.Add(stock);
        return Task.FromResult(true);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(_stocks.Any(s => s.HouseholdId == householdId));

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default) =>
        await work(ct);
}

file sealed class TsProductRepository : IProductRepository
{
    private readonly List<Product> _products = [];

    public void Add(Product p) => _products.Add(p);

    public Task<Product?> FindAsync(ProductId id, CancellationToken ct = default) =>
        Task.FromResult(_products.SingleOrDefault(p => p.Id == id));

    public Task<Product?> FindByNameAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(_products.SingleOrDefault(p =>
            p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)));

    public Task<List<Product>> ListActiveAsync(CancellationToken ct = default) =>
        Task.FromResult(_products.Where(p => !p.IsArchived).ToList());

    public Task<List<Product>> ListActiveWithSkusAsync(CancellationToken ct = default) =>
        Task.FromResult(_products.Where(p => !p.IsArchived).ToList());

    public Task<List<Product>> ListWithConversionsAsync(IEnumerable<ProductId> ids, CancellationToken ct = default) =>
        Task.FromResult(_products.Where(p => ids.Contains(p.Id)).ToList());

    public Task<List<Product>> ListVariantsAsync(ProductId parentId, CancellationToken ct = default) =>
        Task.FromResult(_products.Where(p => p.ParentProductId == parentId).ToList());

    public Task AddAsync(Product product, CancellationToken ct = default) { _products.Add(product); return Task.CompletedTask; }
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

file sealed class TsUnitRepository : IUnitRepository
{
    private readonly List<CatalogUnit> _units = [];

    public void Add(CatalogUnit unit) => _units.Add(unit);

    public Task<CatalogUnit?> FindAsync(UnitId id, CancellationToken ct = default) =>
        Task.FromResult(_units.SingleOrDefault(u => u.Id == id));

    public Task<CatalogUnit?> FindByCodeAsync(string code, CancellationToken ct = default) =>
        Task.FromResult(_units.SingleOrDefault(u => u.Code.Equals(code, StringComparison.OrdinalIgnoreCase)));

    public Task<List<CatalogUnit>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(_units.ToList());

    public Task AddAsync(CatalogUnit unit, CancellationToken ct = default) { _units.Add(unit); return Task.CompletedTask; }
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

file sealed class TsLocationRepository : ILocationRepository
{
    private readonly List<Location> _locs = [];

    public void Add(Location l) => _locs.Add(l);

    public Task<Location?> FindAsync(LocationId id, CancellationToken ct = default) =>
        Task.FromResult(_locs.SingleOrDefault(l => l.Id == id));

    public Task<Location?> FindByNameAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(_locs.SingleOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

    public Task<List<Location>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(_locs.ToList());

    public Task<List<Location>> ListActiveAsync(CancellationToken ct = default) =>
        Task.FromResult(_locs.Where(l => !l.IsArchived).ToList());

    public Task AddAsync(Location l, CancellationToken ct = default) { _locs.Add(l); return Task.CompletedTask; }
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

file sealed class TsPassThroughConversions : IProductConversionProvider
{
    public Task<IQuantityConverter> ForProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult<IQuantityConverter>(new TsIdentityConverter());
}

file sealed class TsIdentityConverter : IQuantityConverter
{
    public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId) => amount;
}
