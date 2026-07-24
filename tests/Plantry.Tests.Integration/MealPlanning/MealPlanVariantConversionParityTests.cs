using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.Pricing.Application;
using Plantry.Pricing.Infrastructure;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.MealPlanning;
using Plantry.Web.Recipes;
using Xunit;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Integration.MealPlanning;

/// <summary>
/// L3 integration tests pinning plantry-xnt5: <see cref="MealPlanWeekReadModel.LoadAsync"/> must widen
/// its conversion load to the variant-inclusive product id set (parity with the DM-19 costing/stock
/// rollup, plantry-daal), so a parent-referencing ingredient whose priced/stocked variant needs a
/// <b>variant-owned</b> cross-dimension (density) conversion bridge to reach the recipe line's unit prices
/// and fulfills correctly in the MealPlan week grid — not just on the Recipe Details page (whose async
/// path reads <c>product_conversions</c> directly per ref and was never affected by this gap).
///
/// Scenario shared by both tests: a parent product ("Peanut Butter") with one variant ("Peanut Butter
/// Jar") stocked/priced in CUPS (volume), while the recipe's ingredient line is authored in GRAMS (mass).
/// Only a conversion owned by the VARIANT's own product id (not the parent's, and not a same-dimension
/// unit pair) bridges cups → grams — exactly the case <see cref="MealPlanWeekReadModel"/>'s old
/// <c>allProductIdList</c>-scoped conversion query missed.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class MealPlanVariantConversionParityTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly IClock Clock = SystemClock.Instance;
    private HouseholdId _household;

    private Guid _gramsId;
    private Guid _cupsId;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        await using var catalog = NewCatalogDb();
        var grams = CatalogUnit.Create(_household, "g", "grams", Dimension.Mass, 1m, isBase: true);
        var cups = CatalogUnit.Create(_household, "cup", "cups", Dimension.Volume, 1m, isBase: true);
        await catalog.Units.AddRangeAsync(grams, cups);
        await catalog.SaveChangesAsync();

        _gramsId = grams.Id.Value;
        _cupsId = cups.Id.Value;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Fulfillment: week-grid stock rollup converts a variant-owned density bridge ────────────────

    [Fact(DisplayName = "plantry-xnt5: week-grid fulfillment converts a parent-referencing line via a variant-owned density bridge (InStock, was Missing)")]
    public async Task WeekGrid_Fulfillment_Converts_ViaVariantOwnedDensityBridge()
    {
        var (parentId, variantId) = await SeedParentVariantWithDensityBridgeAsync(
            parentName: "Peanut Butter",
            variantName: "Peanut Butter Jar",
            variantDefaultUnitId: _cupsId,
            bridgeFromUnitId: _cupsId,
            bridgeToUnitId: _gramsId,
            bridgeFactor: 240m); // 1 cup peanut butter = 240 g (density)

        // 1 cup on hand for the variant — no active lot on the parent (parents can never hold stock).
        var locationId = await SeedLocationAsync("Pantry");
        await EnsureProductStockAsync(variantId);
        await SeedStockEntryAsync(variantId, locationId, quantity: 1m, unitId: _cupsId, expiryDate: null);

        // Recipe references the PARENT, needs 200 g — less than the 240 g the variant's 1 cup converts to.
        var recipeId = await SeedFlatRecipeAsync("Peanut Butter Toast", defaultServings: 1, parentId, quantity: 200m, _gramsId);

        var rm = NewReadModel();
        var bag = await rm.LoadAsync([recipeId], []);

        // Pins the read-model fix directly: the variant's OWN conversion (not the parent's) must be loaded.
        var variantConversions = bag.GetConversions(variantId);
        var bridge = Assert.Single(variantConversions);
        Assert.Equal(_cupsId, bridge.FromUnitId);
        Assert.Equal(_gramsId, bridge.ToUnitId);
        Assert.Equal(240m, bridge.Factor);

        var enricher = new WeekBagEnricher(
            bag,
            new FulfillmentService(new NullStockReader(), new NullCatalogReader(), new NullConverter(), new NullExpiringSoonHorizonReader()),
            new CostingService(new NullPriceReader(), new NullConverter(), new NullCatalogReader()),
            Clock,
            expiringSoonDays: 7);

        var result = enricher.Enrich(recipeId, servings: 1, DateOnly.FromDateTime(DateTime.Today));

        Assert.NotNull(result);
        // 240 g available >= 200 g required → InStock → the sole tracked line is 100% fulfilled.
        // Before the fix, the variant's density bridge was absent from ConversionsByProduct, the
        // conversion call failed, the variant contributed 0 g, and this would read 0% (Missing).
        Assert.Equal(100, result!.FulfillmentPercent);
    }

    // ── Costing: week-grid cost matches the async CostingService path (Recipe Details) ──────────────

    [Fact(DisplayName = "plantry-xnt5: week-grid cost matches the async CostingService path for a variant-owned density-bridge scenario")]
    public async Task WeekGrid_Cost_Matches_AsyncCostingServicePath_ViaVariantOwnedDensityBridge()
    {
        var (parentId, variantId) = await SeedParentVariantWithDensityBridgeAsync(
            parentName: "Olive Oil",
            variantName: "Olive Oil Tin",
            variantDefaultUnitId: _cupsId,
            bridgeFromUnitId: _cupsId,
            bridgeToUnitId: _gramsId,
            bridgeFactor: 240m); // 1 cup = 240 g (density)

        // The variant is priced at $4.80 per cup — only the variant is ever priced (DM-19), never the parent.
        await SeedPriceObservationAsync(variantId, price: 4.80m, quantity: 1m, _cupsId, unitPrice: null, DateTime.UtcNow);

        // Recipe references the PARENT, needs 200 g.
        var recipeId = await SeedFlatRecipeAsync("Roasted Vegetables", defaultServings: 1, parentId, quantity: 200m, _gramsId);

        // ── Week-grid path: MealPlanWeekReadModel.LoadAsync + WeekBagEnricher (pure Compute) ──────────
        var rm = NewReadModel();
        var bag = await rm.LoadAsync([recipeId], []);

        var enricher = new WeekBagEnricher(
            bag,
            new FulfillmentService(new NullStockReader(), new NullCatalogReader(), new NullConverter(), new NullExpiringSoonHorizonReader()),
            new CostingService(new NullPriceReader(), new NullConverter(), new NullCatalogReader()),
            Clock,
            expiringSoonDays: 7);

        var weekGridResult = enricher.Enrich(recipeId, servings: 1, DateOnly.FromDateTime(DateTime.Today));
        Assert.NotNull(weekGridResult);

        // ── Async path: real Recipes ports wired over the real Catalog/Pricing schema — the same
        //    CostingService.ComputeAsync path RecipeReadModelAdapter (Recipe Details, J5) falls back to
        //    for a flat recipe with no inclusions (its expanded path is mathematically equivalent here). ──
        await using var catalogDb = NewCatalogDb();
        await using var pricingDb = NewPricingDb();
        await using var recipesDb = NewRecipesDb();

        var productRepo = new ProductRepository(catalogDb);
        var unitRepo = new UnitRepository(catalogDb);
        var categoryRepo = new CategoryRepository(catalogDb);

        var catalogReader = new CatalogProductReaderAdapter(productRepo, unitRepo, categoryRepo);
        var unitConverter = new RecipesUnitConverterAdapter(productRepo, unitRepo);
        var priceReader = new PriceReaderAdapter(new PricingQueries(new PriceObservationRepository(pricingDb)), Clock);
        var costingServiceAsync = new CostingService(priceReader, unitConverter, catalogReader);

        var recipeRepo = new RecipeRepository(recipesDb);
        var recipe = await recipeRepo.GetByIdAsync(RecipeId.From(recipeId));
        Assert.NotNull(recipe);

        var asyncCost = await costingServiceAsync.ComputeAsync(recipe!, desiredServings: 1);
        var asyncTotalCost = asyncCost.Amount.HasValue ? asyncCost.Amount.Value * 1 : (decimal?)null;

        // Both paths must agree — before the fix, the week grid's ConversionsByProduct lacked the
        // variant's density bridge, the candidate silently dropped out, and the week grid alone would
        // read un-priced ($null / Partial) while Recipe Details priced it correctly ($4.00).
        Assert.Equal(4.00m, asyncTotalCost);
        Assert.Equal(asyncTotalCost, weekGridResult!.TotalCost);
        Assert.False(weekGridResult.CostIsPartial);
    }

    // ── Seeding ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds a parent product (default unit = grams, HasVariants) and one live variant (default unit =
    /// <paramref name="variantDefaultUnitId"/>) carrying a conversion owned by the VARIANT's own product
    /// id — <paramref name="bridgeFromUnitId"/> → <paramref name="bridgeToUnitId"/> at
    /// <paramref name="bridgeFactor"/>. Two save round-trips: the parent must exist before the variant's
    /// FK reference is written.
    /// </summary>
    private async Task<(Guid ParentId, Guid VariantId)> SeedParentVariantWithDensityBridgeAsync(
        string parentName,
        string variantName,
        Guid variantDefaultUnitId,
        Guid bridgeFromUnitId,
        Guid bridgeToUnitId,
        decimal bridgeFactor)
    {
        ProductId parentId;
        await using (var setup = NewCatalogDb())
        {
            var parent = Product.Create(_household, parentName, UnitId.From(_gramsId), Clock);
            parent.SetHasVariants(true, Clock);
            await setup.Products.AddAsync(parent);
            await setup.SaveChangesAsync();
            parentId = parent.Id;
        }

        ProductId variantId;
        await using (var setup = NewCatalogDb())
        {
            var variant = Product.Create(_household, variantName, UnitId.From(variantDefaultUnitId), Clock);
            variant.MakeVariantOf(parentId, Clock);
            variant.AddConversion(UnitId.From(bridgeFromUnitId), UnitId.From(bridgeToUnitId), bridgeFactor, Clock);
            await setup.Products.AddAsync(variant);
            await setup.SaveChangesAsync();
            variantId = variant.Id;
        }

        return (parentId.Value, variantId.Value);
    }

    /// <summary>Seeds a flat recipe (no inclusions) with one ingredient line.</summary>
    private async Task<Guid> SeedFlatRecipeAsync(
        string name, int defaultServings, Guid productId, decimal quantity, Guid unitId)
    {
        await using var ctx = NewRecipesDb();
        var repo = new RecipeRepository(ctx);
        var recipe = Recipe.Create(_household, name, defaultServings, Clock).Value;
        recipe.ReplaceIngredients([new IngredientLine(productId, quantity, unitId, null, 0)], Clock);
        await repo.AddAsync(recipe);
        await repo.SaveChangesAsync();
        return recipe.Id.Value;
    }

    private async Task<Guid> SeedLocationAsync(string name)
    {
        await using var catalog = NewCatalogDb();
        var location = Location.Create(_household, name, LocationType.Ambient);
        await catalog.Locations.AddAsync(location);
        await catalog.SaveChangesAsync();
        return location.Id.Value;
    }

    private async Task EnsureProductStockAsync(Guid productId)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO inventory.product_stock (household_id, product_id, created_at, updated_at)
            VALUES (@hid, @pid, NOW(), NOW())
            ON CONFLICT (household_id, product_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("hid", _household.Value);
        cmd.Parameters.AddWithValue("pid", productId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedStockEntryAsync(
        Guid productId, Guid locationId, decimal quantity, Guid unitId, DateOnly? expiryDate, bool depleted = false)
    {
        await EnsureProductStockAsync(productId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        var depletedAt = depleted ? (object)DateTime.UtcNow : DBNull.Value;
        var expiryObj = expiryDate.HasValue ? (object)expiryDate.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;

        cmd.CommandText = """
            INSERT INTO inventory.stock_entry
                (entry_id, household_id, product_id, location_id, quantity, unit_id, expiry_date,
                 is_open, created_at, updated_at, depleted_at, purchased_at)
            VALUES
                (@id, @hid, @pid, @lid, @qty, @uid, @exp,
                 false, NOW(), NOW(), @dep, NOW())
            """;
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("hid", _household.Value);
        cmd.Parameters.AddWithValue("pid", productId);
        cmd.Parameters.AddWithValue("lid", locationId);
        cmd.Parameters.AddWithValue("qty", quantity);
        cmd.Parameters.AddWithValue("uid", unitId);
        cmd.Parameters.AddWithValue("exp", expiryObj);
        cmd.Parameters.AddWithValue("dep", depletedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<Guid> SeedPriceObservationAsync(
        Guid productId, decimal price, decimal quantity, Guid unitId, decimal? unitPrice, DateTime observedAt)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var id = Guid.NewGuid();
        await using var cmd = conn.CreateCommand();
        var unitPriceObj = unitPrice.HasValue ? (object)unitPrice.Value : DBNull.Value;

        cmd.CommandText = """
            INSERT INTO pricing.price_observation
                (observation_id, household_id, product_id, price, quantity, unit_id, unit_price,
                 source, source_ref, observed_at, user_id)
            VALUES
                (@id, @hid, @pid, @price, @qty, @uid, @up,
                 'Purchase', @ref, @obs, @usr)
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("hid", _household.Value);
        cmd.Parameters.AddWithValue("pid", productId);
        cmd.Parameters.AddWithValue("price", price);
        cmd.Parameters.AddWithValue("qty", quantity);
        cmd.Parameters.AddWithValue("uid", unitId);
        cmd.Parameters.AddWithValue("up", unitPriceObj);
        cmd.Parameters.AddWithValue("ref", Guid.NewGuid());
        cmd.Parameters.AddWithValue("obs", observedAt);
        cmd.Parameters.AddWithValue("usr", Guid.NewGuid());
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private MealPlanWeekReadModel NewReadModel()
    {
        var tenant = new TenantContext();
        tenant.Set(_household.Value);
        return new MealPlanWeekReadModel(db.ConnectionString, tenant, Clock);
    }

    private CatalogDbContext NewCatalogDb()
    {
        var ctx = new CatalogDbContext(
            new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private RecipesDbContext NewRecipesDb()
    {
        var ctx = new RecipesDbContext(
            new DbContextOptionsBuilder<RecipesDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private PricingDbContext NewPricingDb()
    {
        var ctx = new PricingDbContext(
            new DbContextOptionsBuilder<PricingDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    // ── Null-returning port fakes for the pure WeekBagEnricher.Enrich path ─────────────────────────
    // WeekBagEnricher only invokes the pure Compute overloads (all data supplied via the pre-loaded
    // WeekBag), so these ports are never actually called — mirrors WeekBagEnricherTests' fakes.

    private sealed class NullStockReader : IInventoryStockReader
    {
        public Task<ProductStock?> FindStockAsync(Guid productId, CancellationToken ct = default) =>
            Task.FromResult<ProductStock?>(null);
        public Task<IReadOnlyDictionary<Guid, ProductStock>> FindStockBatchAsync(IReadOnlyList<Guid> productIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ProductStock>>(new Dictionary<Guid, ProductStock>());
    }

    private sealed class NullExpiringSoonHorizonReader : IExpiringSoonHorizonReader
    {
        public Task<int> GetDaysAsync(CancellationToken ct = default) => Task.FromResult(7);
    }

    private sealed class NullCatalogReader : ICatalogProductReader
    {
        public Task<CatalogProduct?> FindAsync(Guid productId, CancellationToken ct = default) =>
            Task.FromResult<CatalogProduct?>(null);
        public Task<IReadOnlyList<CatalogProductCandidate>> SearchAsync(string nameQuery, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogProductCandidate>>([]);
        public Task<IReadOnlyDictionary<Guid, CatalogProductSummary>> ResolveSummariesAsync(IReadOnlyList<Guid> productIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, CatalogProductSummary>>(new Dictionary<Guid, CatalogProductSummary>());
        public Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(IReadOnlyList<Guid> unitIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
        public Task<IReadOnlyList<CatalogUnitOption>> ListUnitsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogUnitOption>>([]);
        public Task<IReadOnlyList<CatalogGroupOption>> ListGroupsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogGroupOption>>([]);
        public Task<IReadOnlyList<CatalogCategoryOption>> ListCategoriesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogCategoryOption>>([]);
    }

    private sealed class NullConverter : IUnitConverter
    {
        public Task<Result<decimal>> ConvertAsync(Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default) =>
            Task.FromResult(Result<decimal>.Success(amount));
    }

    private sealed class NullPriceReader : IPriceReader
    {
        public Task<PricePoint?> FindLatestAsync(Guid productId, CancellationToken ct = default) =>
            Task.FromResult<PricePoint?>(null);
    }
}
