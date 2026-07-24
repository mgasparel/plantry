using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.Inventory.Infrastructure;
using Plantry.MealPlanning.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.Inventory;
using Plantry.Web.MealPlanning;
using Xunit;
using CatalogUnit = Plantry.Catalog.Domain.Unit;
using InventoryProductStock = Plantry.Inventory.Domain.ProductStock;
using CatalogCategoryRepository = Plantry.Catalog.Infrastructure.CategoryRepository;
using CatalogLocationRepository = Plantry.Catalog.Infrastructure.LocationRepository;

namespace Plantry.Tests.Integration.MealPlanning;

/// <summary>
/// L3 integration test for the product-dish "Eat" write path (plantry-zcbx): proves the real
/// <see cref="MealPlanEatWriterAdapter"/> against a live Postgres database — the eat/undo/re-eat
/// idempotency token scheme, shortfall-tolerant no-stock handling, and undo restoring exactly what
/// was actually deducted (not the blanket requested quantity). Derived-state netting itself (how
/// <c>IMealPlanCookStatusReader</c> reads these rows back) is covered by
/// <c>MealPlanCookStatusReaderAdapterTests</c>; this suite proves the WRITE side only.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class MealPlanEatWriterAdapterTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly IClock Clock = SystemClock.Instance;
    private HouseholdId _household;
    private readonly Guid _userId = Guid.CreateVersion7();
    private Guid _productId;
    private Guid _unitId;
    private Guid _locationId;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        await using var catalogDb = NewCatalogDb();
        var each = CatalogUnit.Create(_household, "ea", "each", Dimension.Count, 1m, isBase: true);
        await catalogDb.Units.AddAsync(each);
        var product = Product.Create(_household, "Frozen Naan", each.Id, Clock);
        await catalogDb.Products.AddAsync(product);
        var location = Location.Create(_household, "Freezer", LocationType.Frozen);
        await catalogDb.Locations.AddAsync(location);
        await catalogDb.SaveChangesAsync();

        _unitId = each.Id.Value;
        _productId = product.Id.Value;
        _locationId = location.Id.Value;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact(DisplayName = "Eat consumes the requested quantity and stamps an Eat-sourced journal row keyed by the plan dish")]
    public async Task Eat_Consumes_And_Stamps_Journal_Row()
    {
        var plannedDishId = Guid.CreateVersion7();
        await SeedStockAsync(4m);

        await BuildAdapter().EatAsync(plannedDishId, _productId, quantity: 1m, _userId);

        var loaded = await LoadStockAsync();
        var row = Assert.Single(EatRows(loaded));
        Assert.Equal(-1m, row.Delta);
        Assert.Equal(StockReason.Consumed, row.Reason);
        Assert.Equal(StockSourceType.Eat, row.SourceType);
        Assert.Equal(plannedDishId, row.SourceRef);

        var lot = Assert.Single(loaded.Entries);
        Assert.Equal(3m, lot.Quantity);
    }

    // ── Double-tap idempotency ───────────────────────────────────────────────
    // "Double-tap" is modeled as two genuinely CONCURRENT requests (two independent adapter instances
    // racing on the same dish), not two sequentially-awaited calls: the Cook strip only ever renders an
    // "Eat" button for a still-PENDING dish, so a real double-click fires two overlapping requests
    // against the same pre-swap DOM — by the time any response comes back and the button would
    // disappear, a THIRD click is a deliberate, later re-eat (n+1), not a duplicate of the first. The
    // token scheme's guarantee is exactly this race window (both requests read the same "before" n) —
    // proven below, and the sanctioned mechanism it reuses (ProductStock.Consume's (SourceRef,
    // SourceLineRef) short-circuit) is the same one plantry-292a/fks already relies on.

    [Fact(DisplayName = "Two racing eat requests (double-tap) still write exactly one journal entry")]
    public async Task Concurrent_Eat_Race_Is_Idempotent()
    {
        var plannedDishId = Guid.CreateVersion7();
        await SeedStockAsync(4m);

        // Two independent adapter instances (separate DbContexts, mirroring two separate concurrent
        // HTTP requests) both told to eat this dish for the first time — both compute the same n = 1
        // token; the loser's ConsumeStockCommand row-lock blocks, then finds the token already
        // recorded and short-circuits to a no-op.
        await Task.WhenAll(
            BuildAdapter().EatAsync(plannedDishId, _productId, quantity: 1m, _userId),
            BuildAdapter().EatAsync(plannedDishId, _productId, quantity: 1m, _userId));

        var loaded = await LoadStockAsync();
        var row = Assert.Single(EatRows(loaded));
        Assert.Equal(-1m, row.Delta);
        var lot = Assert.Single(loaded.Entries);
        Assert.Equal(3m, lot.Quantity);
    }

    // ── Undo ──────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "Undo restores stock via a compensating Correction add, and eat works again afterwards (re-eat token n+1)")]
    public async Task Undo_Restores_Stock_And_ReEat_Uses_A_Fresh_Token()
    {
        var plannedDishId = Guid.CreateVersion7();
        await SeedStockAsync(4m);
        var adapter = BuildAdapter();

        await adapter.EatAsync(plannedDishId, _productId, quantity: 1m, _userId);
        await adapter.UndoEatAsync(plannedDishId, _productId, quantity: 1m, _userId);

        var afterUndo = await LoadStockAsync();
        var afterUndoRows = EatRows(afterUndo);
        Assert.Equal(2, afterUndoRows.Count); // one Consumed, one compensating Correction
        var undoRow = afterUndoRows.Single(j => j.Delta > 0);
        Assert.Equal(1m, undoRow.Delta);
        Assert.Equal(StockReason.Correction, undoRow.Reason);
        Assert.Equal(StockSourceType.Eat, undoRow.SourceType);
        Assert.Equal(plannedDishId, undoRow.SourceRef);
        // Net stock is back to the original 4 (may be split across two active lots after the FEFO
        // deduction + fresh compensating lot — sum is what matters here).
        Assert.Equal(4m, afterUndo.Entries.Where(e => e.IsActive).Sum(e => e.Quantity));

        // Re-eat: a fresh token (n=2), a fresh negative journal row — not deduped against eat n=1.
        await adapter.EatAsync(plannedDishId, _productId, quantity: 1m, _userId);
        var afterReEat = await LoadStockAsync();
        var afterReEatRows = EatRows(afterReEat);
        Assert.Equal(3, afterReEatRows.Count);
        Assert.Equal(2, afterReEatRows.Count(j => j.Delta < 0));
    }

    [Fact(DisplayName = "Two racing undo requests (double-tap) still write exactly one compensating entry")]
    public async Task Concurrent_Undo_Race_Is_Idempotent()
    {
        var plannedDishId = Guid.CreateVersion7();
        await SeedStockAsync(4m);
        await BuildAdapter().EatAsync(plannedDishId, _productId, quantity: 1m, _userId);

        // The done row only ever renders one "Undo" button — a real double-tap on it is the same
        // overlapping-requests race as the Eat button, so this races two independent adapter instances
        // (separate DbContexts) exactly like Concurrent_Eat_Race_Is_Idempotent above.
        await Task.WhenAll(
            BuildAdapter().UndoEatAsync(plannedDishId, _productId, quantity: 1m, _userId),
            BuildAdapter().UndoEatAsync(plannedDishId, _productId, quantity: 1m, _userId));

        var loaded = await LoadStockAsync();
        Assert.Equal(2, EatRows(loaded).Count); // still just one Consumed + one Correction
        Assert.Equal(4m, loaded.Entries.Where(e => e.IsActive).Sum(e => e.Quantity));
    }

    [Fact(DisplayName = "Undo restores exactly what was actually deducted, not the blanket requested quantity, under a partial shortfall")]
    public async Task Undo_Restores_Only_The_Actually_Deducted_Amount_Under_Shortfall()
    {
        var plannedDishId = Guid.CreateVersion7();
        await SeedStockAsync(0.5m); // only half a unit in stock
        var adapter = BuildAdapter();

        await adapter.EatAsync(plannedDishId, _productId, quantity: 2m, _userId); // requests 2, only 0.5 available

        var afterEat = await LoadStockAsync();
        Assert.Equal(0m, afterEat.Entries.Sum(e => e.Quantity)); // fully drained, no over-deduction

        await adapter.UndoEatAsync(plannedDishId, _productId, quantity: 2m, _userId);

        var afterUndo = await LoadStockAsync();
        // Restored exactly the 0.5 that was actually removed — NOT the full 2 requested.
        Assert.Equal(0.5m, afterUndo.Entries.Where(e => e.IsActive).Sum(e => e.Quantity));
    }

    [Fact(DisplayName = "Undo with no outstanding eat is a no-op")]
    public async Task Undo_With_Nothing_To_Undo_Is_NoOp()
    {
        var plannedDishId = Guid.CreateVersion7();
        await SeedStockAsync(4m);

        await BuildAdapter().UndoEatAsync(plannedDishId, _productId, quantity: 1m, _userId);

        var loaded = await LoadStockAsync();
        Assert.Empty(EatRows(loaded));
        Assert.Equal(4m, loaded.Entries.Single().Quantity);
    }

    // ── Unit-mismatch netting (plantry-wiv2) ────────────────────────────────────
    // The compensating undo ADD must restore each lot in that lot's OWN unit (mirroring the eat's own
    // journal rows), not a single ADD in the product's default unit — otherwise, whenever the default
    // unit differs from the stock lot's unit, MealPlanCookStatusReaderAdapter's raw (unconverted) net
    // of journal Delta stays non-zero after undo and the dish never derives back to pending, even
    // though the physical stock was correctly restored.

    [Theory(DisplayName = "Undo nets the raw journal movement back to exactly zero even when the product's default unit differs from the stock lot's unit, in both directions")]
    [InlineData(true)]  // default unit is the LARGER unit (kg), lot unit is the SMALLER (g) — the ticket's own repro shape
    [InlineData(false)] // default unit is the SMALLER unit (g), lot unit is the LARGER (kg)
    public async Task Undo_Nets_Journal_To_Zero_When_Default_Unit_Differs_From_Lot_Unit(bool defaultUnitIsLarger)
    {
        var gram = CatalogUnit.Create(_household, "g", "gram", Dimension.Mass, 1m, isBase: true);
        var kilogram = CatalogUnit.Create(_household, "kg", "kilogram", Dimension.Mass, 1000m);
        var defaultUnit = defaultUnitIsLarger ? kilogram : gram;
        var lotUnit = defaultUnitIsLarger ? gram : kilogram;

        await using (var catalogDb = NewCatalogDb())
        {
            await catalogDb.Units.AddRangeAsync(gram, kilogram);
            await catalogDb.SaveChangesAsync();
        }

        Guid productId, locationId;
        await using (var catalogDb = NewCatalogDb())
        {
            var product = Product.Create(_household, "Flour", defaultUnit.Id, Clock);
            await catalogDb.Products.AddAsync(product);
            var location = Location.Create(_household, "Pantry", LocationType.Ambient);
            await catalogDb.Locations.AddAsync(location);
            await catalogDb.SaveChangesAsync();
            productId = product.Id.Value;
            locationId = location.Id.Value;
        }

        // 3000 g of stock, expressed in the LOT's own unit (3000 g, or 3 kg).
        var lotQuantity = lotUnit.Id == gram.Id ? 3000m : 3m;
        await using (var invDb = NewInventoryDb())
        {
            var stock = InventoryProductStock.Start(_household, productId, Clock);
            stock.AddStock(lotQuantity, lotUnit.Id.Value, locationId, _userId, Clock);
            await invDb.ProductStocks.AddAsync(stock);
            await invDb.SaveChangesAsync();
        }

        var plannedDishId = Guid.CreateVersion7();
        var adapter = BuildAdapter();

        // Eat 2000 g worth, expressed in the product's DEFAULT unit (2 kg or 2000 g).
        var eatQuantity = defaultUnitIsLarger ? 2m : 2000m;
        await adapter.EatAsync(plannedDishId, productId, eatQuantity, _userId);

        var loaded = await LoadStockDirectAsync(productId);
        var eatRow = Assert.Single(EatRows(loaded));
        Assert.Equal(lotUnit.Id.Value, eatRow.UnitId); // written in the LOT's own unit, not the default unit
        Assert.True(EatRows(loaded).Sum(j => j.Delta) < 0m); // net negative — dish reads as "eaten"

        await adapter.UndoEatAsync(plannedDishId, productId, eatQuantity, _userId);

        var afterUndo = await LoadStockDirectAsync(productId);
        var undoRows = EatRows(afterUndo);
        Assert.Equal(2, undoRows.Count); // one Consumed, one compensating Correction
        var undoRow = undoRows.Single(j => j.Delta > 0);
        Assert.Equal(lotUnit.Id.Value, undoRow.UnitId); // restored in the LOT's own unit
        Assert.Equal(-eatRow.Delta, undoRow.Delta); // exact per-row cancellation

        // The raw net — exactly what MealPlanCookStatusReaderAdapter sums, no unit conversion — must
        // return to zero (≥ 0), or the dish stays stuck "eaten".
        Assert.Equal(0m, undoRows.Sum(j => j.Delta));
        Assert.Equal(lotQuantity, afterUndo.Entries.Where(e => e.IsActive).Sum(e => e.Quantity)); // physical stock also whole again
    }

    // ── Shortfall tolerance (C8/R9 mirror) ──────────────────────────────────────

    [Fact(DisplayName = "Eat on a never-stocked product never throws (shortfall-tolerant no-op)")]
    public async Task Eat_On_NeverStocked_Product_Never_Throws()
    {
        var plannedDishId = Guid.CreateVersion7();
        // No SeedStockAsync call — the product has no ProductStock record at all.

        var exception = await Record.ExceptionAsync(() =>
            BuildAdapter().EatAsync(plannedDishId, _productId, quantity: 1m, _userId));

        Assert.Null(exception);
    }

    [Fact(DisplayName = "Eat reports a partial shortfall as a partial consume, never over-deducting")]
    public async Task Eat_Consumes_Partial_Stock_Without_Blocking()
    {
        var plannedDishId = Guid.CreateVersion7();
        await SeedStockAsync(0.5m);

        await BuildAdapter().EatAsync(plannedDishId, _productId, quantity: 2m, _userId);

        var loaded = await LoadStockAsync();
        var row = Assert.Single(EatRows(loaded));
        Assert.Equal(-0.5m, row.Delta); // only what was available, never a negative-2 over-deduction
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Journal rows written by the Eat/Undo write path itself — excludes the seed AddStock's own Manual-sourced row.</summary>
    private static List<StockJournalEntry> EatRows(InventoryProductStock stock) =>
        stock.Journal.Where(j => j.SourceType == StockSourceType.Eat).ToList();

    private async Task SeedStockAsync(decimal quantity)
    {
        await using var invDb = NewInventoryDb();
        var stock = InventoryProductStock.Start(_household, _productId, Clock);
        stock.AddStock(quantity, _unitId, _locationId, _userId, Clock);
        await invDb.ProductStocks.AddAsync(stock);
        await invDb.SaveChangesAsync();
    }

    private async Task<InventoryProductStock> LoadStockAsync() => await LoadStockDirectAsync(_productId);

    private async Task<InventoryProductStock> LoadStockDirectAsync(Guid productId)
    {
        await using var verify = NewInventoryDb();
        return await verify.ProductStocks
            .Include(p => p.Entries)
            .Include(p => p.Journal)
            .SingleAsync(p => p.ProductId == productId);
    }

    private IMealPlanEatWriter BuildAdapter()
    {
        var invDb = NewInventoryDb();
        var catDb = NewCatalogDb();
        var productRepo = new Plantry.Catalog.Infrastructure.ProductRepository(catDb);
        var unitRepo = new Plantry.Catalog.Infrastructure.UnitRepository(catDb);
        var categoryRepo = new CatalogCategoryRepository(catDb);
        var locationRepo = new CatalogLocationRepository(catDb);
        var conversions = new CatalogConversionProvider(productRepo, unitRepo);
        var catalog = new CatalogReadFacade(productRepo, unitRepo, categoryRepo, locationRepo);
        var stocks = new ProductStockRepository(invDb);
        var journalReader = new JournalEntriesBySourceRefReader(invDb);
        var tenant = new TestTenant(_household.Value);
        return new MealPlanEatWriterAdapter(
            stocks, catalog, conversions, journalReader, locationRepo, Clock, tenant,
            NullLogger<ConsumeStockCommand>.Instance, NullLogger<AddStockCommand>.Instance);
    }

    private InventoryDbContext NewInventoryDb()
    {
        var ctx = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private CatalogDbContext NewCatalogDb()
    {
        var ctx = new CatalogDbContext(
            new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private sealed class TestTenant(Guid household) : ITenantContext
    {
        public Guid? HouseholdId { get; } = household;
    }
}
