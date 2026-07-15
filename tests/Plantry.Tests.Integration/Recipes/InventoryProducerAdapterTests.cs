using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.Inventory.Infrastructure;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.Inventory;
using Plantry.Web.Recipes;
using Xunit;
using CatalogUnit = Plantry.Catalog.Domain.Unit;
using InventoryProductStock = Plantry.Inventory.Domain.ProductStock;
using CatalogCategoryRepository = Plantry.Catalog.Infrastructure.CategoryRepository;
using CatalogLocationRepository = Plantry.Catalog.Infrastructure.LocationRepository;

namespace Plantry.Tests.Integration.Recipes;

/// <summary>
/// L3 integration test for the yield-on-cook produce path (plantry-854a, Gate 10B / plantry-v0p).
/// The real <see cref="InventoryProducerAdapter"/> was previously proven only at L1 domain
/// (<c>ProductStockAddStockTests</c>) and via recording fakes at L4 (<c>CookOnPostYieldTests</c>) —
/// never end-to-end through the real EF repo + a live Postgres DB with RLS. This harness wires the
/// adapter across the Recipes/Inventory seam against a real database and proves:
/// <list type="bullet">
/// <item>a produce lands exactly one lot stamped <see cref="StockSourceType.Cook"/> in the household's
/// first active location, carrying the user-supplied expiry (ADR-011);</item>
/// <item>a re-driven <see cref="ReconcilePendingCooks"/> (interrupted-then-reconciled cook) does not
/// double-add — a single lot + single journal row survive via the (source_ref, source_line_ref)
/// idempotency short-circuit (plantry-292a / plantry-fks);</item>
/// <item>the produced lot is RLS-scoped to the cooking household — proven over a non-superuser
/// app_user connection so Postgres RLS (not the EF query filter) is what hides the produced rows from
/// another household.</item>
/// </list>
/// FEFO / add-lot mechanics are covered by the Inventory unit tests; this test proves the adapter
/// wiring, source stamping, location resolution, idempotency, and tenancy end-to-end.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class InventoryProducerAdapterTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly IClock Clock = SystemClock.Instance;
    private HouseholdId _household;
    private readonly Guid _userId = Guid.CreateVersion7();
    private Guid _productId;
    private Guid _unitId;

    // The household's active locations, alphabetically ordered — the adapter stores yield in the first.
    private Guid _firstActiveLocationId;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        // Seed a unit + a stock-holding yield product in Catalog so the adapter's AddStockCommand can
        // resolve the product, and TWO active locations so "first active location" is a deterministic
        // choice (LocationRepository.ListActiveAsync orders by Name) rather than an accident of seeding.
        await using var catalogDb = NewCatalogDb(_household);
        var grams = CatalogUnit.Create(_household, "g", "grams", Dimension.Mass, 1m, isBase: true);
        await catalogDb.Units.AddAsync(grams);
        var product = Product.Create(_household, "Cooked Rice", grams.Id, Clock);
        await catalogDb.Products.AddAsync(product);

        // "Alpha Pantry" sorts before "Zeta Pantry", so it is the first active location.
        var alpha = Location.Create(_household, "Alpha Pantry", LocationType.Ambient);
        var zeta = Location.Create(_household, "Zeta Pantry", LocationType.Ambient);
        await catalogDb.Locations.AddAsync(alpha);
        await catalogDb.Locations.AddAsync(zeta);
        await catalogDb.SaveChangesAsync();

        _unitId = grams.Id.Value;
        _productId = product.Id.Value;
        _firstActiveLocationId = alpha.Id.Value;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Criterion 1: one Cook-sourced lot, first active location, user expiry ───────

    [Fact(DisplayName = "Produce adds one Cook-sourced lot in the first active location with the user-supplied expiry")]
    public async Task Produce_Adds_One_Cook_Sourced_Lot_In_First_Active_Location_With_Expiry()
    {
        var cookEventId = Guid.CreateVersion7();
        var sourceLineRef = Guid.CreateVersion7();
        var expiry = new DateOnly(2026, 8, 15);

        await BuildProducer(_household).ProduceAsync(
            _productId, quantity: 400m, _unitId, expiry,
            ProduceReason.Recipe, cookEventId, _userId, sourceLineRef);

        await using var verify = NewInventoryDb(_household);
        var loaded = await verify.ProductStocks
            .Include(p => p.Entries)
            .Include(p => p.Journal)
            .SingleAsync(p => p.ProductId == _productId);

        // Exactly one lot, holding the produced quantity, in the alphabetically-first active location,
        // carrying the user-supplied expiry.
        var lot = Assert.Single(loaded.Entries);
        Assert.Equal(400m, lot.Quantity);
        Assert.Equal(_firstActiveLocationId, lot.LocationId);
        Assert.Equal(expiry, lot.ExpiryDate);
        Assert.True(lot.IsActive);

        // Exactly one journal row: a positive Purchase addition stamped source_type = Cook, with the
        // cook event as source_ref and the produce line as the idempotency source_line_ref.
        var journalRow = Assert.Single(loaded.Journal);
        Assert.Equal(400m, journalRow.Delta);
        Assert.Equal(StockReason.Purchase, journalRow.Reason);
        Assert.Equal(StockSourceType.Cook, journalRow.SourceType);
        Assert.Equal(cookEventId, journalRow.SourceRef);
        Assert.Equal(sourceLineRef, journalRow.SourceLineRef);
    }

    [Fact(DisplayName = "Produce throws when the household has no active storage location")]
    public async Task Produce_Throws_When_No_Active_Location()
    {
        // A household with a product but no location — the deterministic storage fallback has nowhere to go.
        var barren = HouseholdId.New();
        Guid barrenProduct;
        Guid barrenUnit;
        await using (var catalogDb = NewCatalogDb(barren))
        {
            var grams = CatalogUnit.Create(barren, "g", "grams", Dimension.Mass, 1m, isBase: true);
            await catalogDb.Units.AddAsync(grams);
            var product = Product.Create(barren, "Cooked Rice", grams.Id, Clock);
            await catalogDb.Products.AddAsync(product);
            await catalogDb.SaveChangesAsync();
            barrenProduct = product.Id.Value;
            barrenUnit = grams.Id.Value;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BuildProducer(barren).ProduceAsync(
                barrenProduct, quantity: 100m, barrenUnit, expiryDate: null,
                ProduceReason.Recipe, Guid.CreateVersion7(), _userId, Guid.CreateVersion7()));
    }

    // ── Criterion 2: re-driven ReconcilePendingCooks does not double-add ───────────

    [Fact(DisplayName = "Re-driven ReconcilePendingCooks does not double-add — single lot + single journal row survive")]
    public async Task Redriven_ReconcilePendingCooks_Does_Not_Double_Add()
    {
        var expiry = new DateOnly(2026, 9, 1);

        // Persist a CookEvent whose single yield produce line is still Pending — the state left behind
        // when a cook crashed after the anchor commit (CookEvent + Pending lines) but before the produce
        // line's status transition was saved.
        var recipeId = await SeedRecipeAsync(_household);
        CookEventId cookEventId;
        Guid produceLineId;
        await using (var recipesDb = NewRecipesDb(_household))
        {
            var repo = new CookEventRepository(recipesDb);
            var cookEvent = CookEvent.Record(recipeId, _household, servingsCooked: 4, _userId, Clock).Value;
            var produceLine = cookEvent.AddProduceLine(_productId, 400m, _unitId, expiry);
            cookEventId = cookEvent.Id;
            produceLineId = produceLine.Id.Value;
            await repo.AddAsync(cookEvent);
            await repo.SaveChangesAsync();
        }

        // Simulate the interruption: the original produce ALREADY committed its inventory lot + journal
        // row (same source_ref / source_line_ref the reconciler will use), but the crash prevented the
        // produce line from being marked Applied. Drive the real adapter directly to lay down that row.
        await BuildProducer(_household).ProduceAsync(
            _productId, quantity: 400m, _unitId, expiry,
            ProduceReason.Recipe, cookEventId.Value, _userId, sourceLineRef: produceLineId);

        // Now re-drive through the REAL ReconcilePendingCooks: it re-issues the produce for the still-Pending
        // line. The Inventory add must short-circuit on the (source_ref, source_line_ref) pair — no second lot,
        // no second journal row — and the line transitions to Applied.
        var result = await BuildReconciler(_household).ExecuteAsync();
        Assert.Equal(1, result.ReconciledLineCount);

        // Inventory: still exactly one lot and one journal row — the re-drive was an idempotent no-op.
        await using (var verify = NewInventoryDb(_household))
        {
            var loaded = await verify.ProductStocks
                .Include(p => p.Entries)
                .Include(p => p.Journal)
                .SingleAsync(p => p.ProductId == _productId);

            var lot = Assert.Single(loaded.Entries);
            Assert.Equal(400m, lot.Quantity); // not 800 — no double-add
            var journalRow = Assert.Single(loaded.Journal);
            Assert.Equal(400m, journalRow.Delta);
            Assert.Equal(StockSourceType.Cook, journalRow.SourceType);
            Assert.Equal(cookEventId.Value, journalRow.SourceRef);
            Assert.Equal(produceLineId, journalRow.SourceLineRef);
        }

        // Recipes: the produce line is now Applied (reconciliation persisted the transition).
        await using (var verifyRecipes = NewRecipesDb(_household))
        {
            var reloaded = await verifyRecipes.CookEvents
                .Include(c => c.ProduceLines)
                .SingleAsync(c => c.Id == cookEventId);
            var produceLine = Assert.Single(reloaded.ProduceLines);
            Assert.Equal(CookProduceLineStatus.Applied, produceLine.Status);
        }
    }

    [Fact(DisplayName = "ReconcilePendingCooks applies a never-run pending produce line, adding the lot exactly once")]
    public async Task ReconcilePendingCooks_Applies_A_NeverRun_Pending_Produce_Line()
    {
        var expiry = new DateOnly(2026, 9, 1);
        var recipeId = await SeedRecipeAsync(_household);

        // A cook whose produce line never ran at all (no prior inventory row) — reconciliation adds it now.
        await using (var recipesDb = NewRecipesDb(_household))
        {
            var repo = new CookEventRepository(recipesDb);
            var cookEvent = CookEvent.Record(recipeId, _household, servingsCooked: 4, _userId, Clock).Value;
            cookEvent.AddProduceLine(_productId, 250m, _unitId, expiry);
            await repo.AddAsync(cookEvent);
            await repo.SaveChangesAsync();
        }

        var result = await BuildReconciler(_household).ExecuteAsync();
        Assert.Equal(1, result.ReconciledLineCount);

        await using var verify = NewInventoryDb(_household);
        var loaded = await verify.ProductStocks
            .Include(p => p.Entries)
            .Include(p => p.Journal)
            .SingleAsync(p => p.ProductId == _productId);
        var lot = Assert.Single(loaded.Entries);
        Assert.Equal(250m, lot.Quantity);
        Assert.Equal(_firstActiveLocationId, lot.LocationId);
        Assert.Equal(expiry, lot.ExpiryDate);
        Assert.Equal(StockSourceType.Cook, Assert.Single(loaded.Journal).SourceType);

        // A second reconcile finds nothing to do — the line is Applied, not Pending — so nothing re-runs.
        var second = await BuildReconciler(_household).ExecuteAsync();
        Assert.Equal(0, second.ReconciledLineCount);
    }

    // ── Criterion 3: produced lot is RLS-scoped to the cooking household ───────────

    [Fact(DisplayName = "Produced lot is RLS-scoped to the cooking household — another household cannot read it")]
    public async Task Produced_Lot_Is_Rls_Scoped_To_Cooking_Household()
    {
        var otherHousehold = HouseholdId.New();
        var cookEventId = Guid.CreateVersion7();

        await BuildProducer(_household).ProduceAsync(
            _productId, quantity: 300m, _unitId, expiryDate: new DateOnly(2026, 8, 1),
            ProduceReason.Recipe, cookEventId, _userId, sourceLineRef: Guid.CreateVersion7());

        // The cooking household reads its own lot, and every persisted row carries its household id.
        await using (var owner = NewInventoryDb(_household))
        {
            var own = await owner.ProductStocks
                .Include(p => p.Entries)
                .Include(p => p.Journal)
                .SingleAsync(p => p.ProductId == _productId);
            Assert.Equal(_household, own.HouseholdId);
            Assert.All(own.Entries, e => Assert.Equal(_household, e.HouseholdId));
            Assert.All(own.Journal, j => Assert.Equal(_household, j.HouseholdId));
        }

        // Postgres RLS backstop over the produced lot: connect as the non-superuser app_user role (RLS
        // never applies to superusers, so this is the only path that actually exercises the policies) and
        // prove every inventory table hides the produced rows from another household. Setting
        // app.household_id to the cooking household first confirms the rows genuinely persisted and are
        // visible to their owner — so the emptiness under the other household is attributable to RLS, not
        // to the lot simply not existing.
        await using var conn = new NpgsqlConnection(db.AppUserConnectionString);
        await conn.OpenAsync();

        await SetHouseholdAsync(conn, _household.Value);
        Assert.True(await CountProducedRowsAsync(conn, "inventory.product_stock") >= 1);
        Assert.True(await CountProducedRowsAsync(conn, "inventory.stock_entry") >= 1);
        Assert.True(await CountProducedRowsAsync(conn, "inventory.stock_journal_entry") >= 1);

        await SetHouseholdAsync(conn, otherHousehold.Value);
        Assert.Equal(0, await CountProducedRowsAsync(conn, "inventory.product_stock"));
        Assert.Equal(0, await CountProducedRowsAsync(conn, "inventory.stock_entry"));
        Assert.Equal(0, await CountProducedRowsAsync(conn, "inventory.stock_journal_entry"));
    }

    private static async Task SetHouseholdAsync(NpgsqlConnection conn, Guid household)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SET app.household_id = '{household}'";
        await cmd.ExecuteNonQueryAsync();
    }

    // Counts rows for the produced product on an app_user connection, so the count reflects what RLS
    // admits for the currently-set app.household_id — not the EF query filter.
    private async Task<int> CountProducedRowsAsync(NpgsqlConnection conn, string table)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE product_id = @productId";
        var p = cmd.CreateParameter();
        p.ParameterName = "productId";
        p.Value = _productId;
        cmd.Parameters.Add(p);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private IInventoryProducer BuildProducer(HouseholdId household)
    {
        var (catalog, stocks, locations, tenant) = BuildInventoryDependencies(household);
        return new InventoryProducerAdapter(
            stocks, catalog, locations, Clock, tenant, NullLogger<AddStockCommand>.Instance);
    }

    private ReconcilePendingCooks BuildReconciler(HouseholdId household)
    {
        var recipesDb = NewRecipesDb(household);
        var cookEvents = new CookEventRepository(recipesDb);

        // Real producer over the Recipes/Inventory seam — the subject of this test.
        var producer = BuildProducer(household);

        // Real consumer, wired so ReconcilePendingCooks is fully composed even though these cooks carry
        // no consume lines (its ctor requires a non-null consumer).
        var (catalog, stocks, _, tenant) = BuildInventoryDependencies(household);
        var catDb = NewCatalogDb(household);
        var conversions = new CatalogConversionProvider(
            new Plantry.Catalog.Infrastructure.ProductRepository(catDb),
            new Plantry.Catalog.Infrastructure.UnitRepository(catDb));
        var consumer = new InventoryConsumerAdapter(
            stocks, catalog, conversions, Clock, tenant, NullLogger<ConsumeStockCommand>.Instance);

        var lineDriver = new CookLineDriver(consumer, producer);
        return new ReconcilePendingCooks(
            cookEvents, lineDriver, tenant, NullLogger<ReconcilePendingCooks>.Instance);
    }

    private (ICatalogReadFacade Catalog, ProductStockRepository Stocks, CatalogLocationRepository Locations, TestTenant Tenant)
        BuildInventoryDependencies(HouseholdId household)
    {
        var invDb = NewInventoryDb(household);
        var catDb = NewCatalogDb(household);
        var productRepo = new Plantry.Catalog.Infrastructure.ProductRepository(catDb);
        var unitRepo = new Plantry.Catalog.Infrastructure.UnitRepository(catDb);
        var categoryRepo = new CatalogCategoryRepository(catDb);
        var locationRepo = new CatalogLocationRepository(catDb);
        var catalog = new CatalogReadFacade(productRepo, unitRepo, categoryRepo, locationRepo);
        var stocks = new ProductStockRepository(invDb);
        var tenant = new TestTenant(household.Value);
        return (catalog, stocks, locationRepo, tenant);
    }

    private async Task<RecipeId> SeedRecipeAsync(HouseholdId household)
    {
        await using var ctx = NewRecipesDb(household);
        var repo = new RecipeRepository(ctx);
        var recipe = Recipe.Create(household, "Yield-declaring Recipe", 4, Clock).Value;
        recipe.ReplaceIngredients(
            [new IngredientLine(Guid.CreateVersion7(), 1m, Guid.CreateVersion7(), null, 0)], Clock);
        await repo.AddAsync(recipe);
        await repo.SaveChangesAsync();
        return recipe.Id;
    }

    private InventoryDbContext NewInventoryDb(HouseholdId household)
    {
        var ctx = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }

    private CatalogDbContext NewCatalogDb(HouseholdId household)
    {
        var ctx = new CatalogDbContext(
            new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }

    private RecipesDbContext NewRecipesDb(HouseholdId household)
    {
        var ctx = new RecipesDbContext(
            new DbContextOptionsBuilder<RecipesDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }

    private sealed class TestTenant(Guid household) : ITenantContext
    {
        public Guid? HouseholdId { get; } = household;
    }
}
