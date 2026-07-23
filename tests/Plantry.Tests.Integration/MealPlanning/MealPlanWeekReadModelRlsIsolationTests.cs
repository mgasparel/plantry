using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.Inventory.Domain;
using Plantry.Inventory.Infrastructure;
using Plantry.Pricing.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.MealPlanning;
using Xunit;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Integration.MealPlanning;

/// <summary>
/// RLS isolation tests for <see cref="MealPlanWeekReadModel"/> (ADR-021, plantry-nz3u.4).
///
/// Proves that the cross-schema read model — which bypasses per-context EF HasQueryFilter
/// by design (ADR-021) — does not leak data across households. Connection-level Postgres
/// RLS (ADR-008) is the sole tenant backstop for this path; these tests verify it works.
///
/// Test coverage:
/// <list type="bullet">
///   <item>Recipes, ingredients, and products from household B are not visible to household A.</item>
///   <item>Stock from household B is not visible to household A.</item>
///   <item>Price observations from household B are not visible to household A.</item>
///   <item>Units from household B are not visible to household A (catalog.units has household_id).</item>
///   <item>No tenant context (empty app.household_id) returns an empty bag — RLS strict policy.</item>
/// </list>
///
/// The read model arms RLS using parameterized set_config (mirroring HouseholdRlsConnectionInterceptor);
/// these tests use <see cref="PostgresFixture.AppUserConnectionString"/> so RLS policies apply
/// (RLS is bypassed for superusers — the Testcontainers bootstrap user is one).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class MealPlanWeekReadModelRlsIsolationTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _householdA;
    private HouseholdId _householdB;

    // Shared unit ids per household (units have household_id in catalog.units).
    private Guid _gramsA;
    private Guid _gramsB;

    // Seeded recipe/product ids so tests can assert specific cross-household absence.
    private Guid _recipeIdA;
    private Guid _recipeIdB;
    private Guid _productIdA;
    private Guid _productIdB;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _householdA = HouseholdId.New();
        _householdB = HouseholdId.New();

        // Seed household A.
        _gramsA = await SeedUnitAsync(_householdA, "g", "grams", Dimension.Mass, 1m, isBase: true);
        _productIdA = await SeedProductAsync(_householdA, "Flour A", _gramsA, trackStock: true);
        _recipeIdA = await SeedRecipeAsync(_householdA, "Pasta A", 2, (_productIdA, 200m, _gramsA, 1));
        var locationA = await SeedLocationAsync(_householdA, "Pantry A");
        await SeedStockEntryAsync(_householdA, _productIdA, locationA, 500m, _gramsA);
        await SeedPriceObservationAsync(_householdA, _productIdA, 1.99m, 500m, _gramsA);

        // Seed household B with distinct data.
        _gramsB = await SeedUnitAsync(_householdB, "g", "grams", Dimension.Mass, 1m, isBase: true);
        _productIdB = await SeedProductAsync(_householdB, "Sugar B", _gramsB, trackStock: true);
        _recipeIdB = await SeedRecipeAsync(_householdB, "Cake B", 4, (_productIdB, 300m, _gramsB, 1));
        var locationB = await SeedLocationAsync(_householdB, "Pantry B");
        await SeedStockEntryAsync(_householdB, _productIdB, locationB, 800m, _gramsB);
        await SeedPriceObservationAsync(_householdB, _productIdB, 2.49m, 500m, _gramsB);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── RLS isolation: recipes ───────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "RLS: household A cannot see household B's recipe via the read model")]
    public async Task RlsPolicy_HouseholdA_CannotSee_HouseholdB_Recipe()
    {
        // Run the read model under household A's tenant context.
        // Request household B's recipe id — should return an empty bag (RLS filters the rows).
        var rm = NewReadModel(_householdA);
        var bag = await rm.LoadAsync([_recipeIdB], []);

        Assert.DoesNotContain(_recipeIdB, bag.Recipes.Keys);
        // Household A's recipe is not requested — bag should have no recipes at all.
        Assert.Empty(bag.Recipes);
    }

    [Fact(DisplayName = "RLS: household A's recipe is loaded; household B's is absent even when both requested")]
    public async Task RlsPolicy_HouseholdA_CanSeeOwnRecipe_NotHouseholdBRecipe()
    {
        var rm = NewReadModel(_householdA);
        // Request both recipe ids — RLS must filter household B's row before mapping.
        var bag = await rm.LoadAsync([_recipeIdA, _recipeIdB], []);

        Assert.Contains(_recipeIdA, bag.Recipes.Keys);
        Assert.DoesNotContain(_recipeIdB, bag.Recipes.Keys);
    }

    [Fact(DisplayName = "RLS: household A cannot see household B's recipe ingredients")]
    public async Task RlsPolicy_HouseholdA_CannotSee_HouseholdB_Ingredients()
    {
        var rm = NewReadModel(_householdA);
        var bag = await rm.LoadAsync([_recipeIdA, _recipeIdB], []);

        // Ingredients for household B's recipe must not appear.
        Assert.DoesNotContain(_recipeIdB, bag.IngredientsByRecipe.Keys);
        // Household A's recipe ingredient product id must not be household B's product.
        if (bag.IngredientsByRecipe.TryGetValue(_recipeIdA, out var ingrA))
            Assert.All(ingrA, i => Assert.NotEqual(_productIdB, i.ProductId));
    }

    // ── RLS isolation: products / catalog ───────────────────────────────────────────────────────

    [Fact(DisplayName = "RLS: household A cannot see household B's product via the read model")]
    public async Task RlsPolicy_HouseholdA_CannotSee_HouseholdB_Product()
    {
        var rm = NewReadModel(_householdA);
        // Explicitly request household B's product id.
        var bag = await rm.LoadAsync([], [_productIdB]);

        Assert.DoesNotContain(_productIdB, bag.Products.Keys);
    }

    [Fact(DisplayName = "RLS: household A sees own product; household B's product absent when both requested")]
    public async Task RlsPolicy_HouseholdA_CanSeeOwnProduct_NotHouseholdBProduct()
    {
        var rm = NewReadModel(_householdA);
        var bag = await rm.LoadAsync([], [_productIdA, _productIdB]);

        Assert.Contains(_productIdA, bag.Products.Keys);
        Assert.DoesNotContain(_productIdB, bag.Products.Keys);
    }

    // ── RLS isolation: stock ─────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "RLS: household A cannot see household B's stock via the read model")]
    public async Task RlsPolicy_HouseholdA_CannotSee_HouseholdB_Stock()
    {
        var rm = NewReadModel(_householdA);
        var bag = await rm.LoadAsync([], [_productIdB]);

        // Stock seeded for household B's product must not appear under household A's tenant.
        Assert.DoesNotContain(_productIdB, bag.StockByProduct.Keys);
    }

    [Fact(DisplayName = "RLS: household A sees own stock; household B's stock absent when both products requested")]
    public async Task RlsPolicy_HouseholdA_CanSeeOwnStock_NotHouseholdBStock()
    {
        var rm = NewReadModel(_householdA);
        var bag = await rm.LoadAsync([], [_productIdA, _productIdB]);

        Assert.Contains(_productIdA, bag.StockByProduct.Keys);
        Assert.DoesNotContain(_productIdB, bag.StockByProduct.Keys);
    }

    // ── RLS isolation: prices ────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "RLS: household A cannot see household B's price observations via the read model")]
    public async Task RlsPolicy_HouseholdA_CannotSee_HouseholdB_Price()
    {
        var rm = NewReadModel(_householdA);
        var bag = await rm.LoadAsync([], [_productIdB]);

        Assert.DoesNotContain(_productIdB, bag.LatestPriceByProduct.Keys);
    }

    [Fact(DisplayName = "RLS: household A sees own price; household B's price absent when both products requested")]
    public async Task RlsPolicy_HouseholdA_CanSeeOwnPrice_NotHouseholdBPrice()
    {
        var rm = NewReadModel(_householdA);
        var bag = await rm.LoadAsync([], [_productIdA, _productIdB]);

        Assert.Contains(_productIdA, bag.LatestPriceByProduct.Keys);
        Assert.DoesNotContain(_productIdB, bag.LatestPriceByProduct.Keys);
    }

    // ── RLS isolation: units ─────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "RLS: household A cannot see household B's catalog units via the read model")]
    public async Task RlsPolicy_HouseholdA_CannotSee_HouseholdB_Units()
    {
        var rm = NewReadModel(_householdA);
        var bag = await rm.LoadAsync([], []);

        // The units query has no WHERE clause — RLS is the only filter.
        // Household A's unit id is present; household B's (a different id) is absent.
        Assert.Contains(_gramsA, bag.Units.Keys);
        Assert.DoesNotContain(_gramsB, bag.Units.Keys);
    }

    // ── RLS: no tenant context returns empty bag ─────────────────────────────────────────────────

    [Fact(DisplayName = "RLS: empty tenant context (no household set) returns empty bag — strict policy")]
    public async Task RlsPolicy_NoTenantContext_StrictPolicy_ReturnsEmptyBag()
    {
        // TenantContext with no household set writes app.household_id = '' — RLS strict policy
        // returns no rows. The read model must not surface data from any household.
        var emptyTenant = new TenantContext(); // never called Set()
        var rm = new MealPlanWeekReadModel(db.AppUserConnectionString, emptyTenant, SystemClock.Instance);

        var bag = await rm.LoadAsync([_recipeIdA, _recipeIdB], [_productIdA, _productIdB]);

        Assert.Empty(bag.Recipes);
        Assert.Empty(bag.IngredientsByRecipe);
        Assert.Empty(bag.Products);
        Assert.Empty(bag.StockByProduct);
        Assert.Empty(bag.LatestPriceByProduct);
        Assert.Empty(bag.Units);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="MealPlanWeekReadModel"/> using the non-superuser app_user connection
    /// string so Postgres RLS policies take effect. Superusers bypass RLS — the Testcontainers
    /// bootstrap user is one, so RLS tests must use <see cref="PostgresFixture.AppUserConnectionString"/>.
    /// </summary>
    private MealPlanWeekReadModel NewReadModel(HouseholdId household)
    {
        var tenant = new TenantContext();
        tenant.Set(household.Value);
        return new MealPlanWeekReadModel(db.AppUserConnectionString, tenant, SystemClock.Instance);
    }

    private CatalogDbContext NewCatalogDb(HouseholdId household)
    {
        var opts = new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(db.ConnectionString).Options;
        var ctx = new CatalogDbContext(opts);
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }

    private InventoryDbContext NewInventoryDb(HouseholdId household)
    {
        var opts = new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(db.ConnectionString).Options;
        var ctx = new InventoryDbContext(opts);
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }

    private PricingDbContext NewPricingDb(HouseholdId household)
    {
        var opts = new DbContextOptionsBuilder<PricingDbContext>().UseNpgsql(db.ConnectionString).Options;
        var ctx = new PricingDbContext(opts);
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }

    private async Task<Guid> SeedUnitAsync(
        HouseholdId household, string symbol, string name, Dimension dimension, decimal factorToBase, bool isBase)
    {
        await using var catalog = NewCatalogDb(household);
        var unit = CatalogUnit.Create(household, symbol, name, dimension, factorToBase, isBase: isBase);
        await catalog.Units.AddAsync(unit);
        await catalog.SaveChangesAsync();
        return unit.Id.Value;
    }

    private async Task<Guid> SeedProductAsync(HouseholdId household, string name, Guid defaultUnitId, bool trackStock = true)
    {
        await using var catalog = NewCatalogDb(household);
        var unitId = UnitId.From(defaultUnitId);
        var product = Product.Create(household, name, unitId, SystemClock.Instance, trackStock: trackStock);
        await catalog.Products.AddAsync(product);
        await catalog.SaveChangesAsync();
        return product.Id.Value;
    }

    private async Task<Guid> SeedLocationAsync(HouseholdId household, string name)
    {
        await using var catalog = NewCatalogDb(household);
        var location = Location.Create(household, name, LocationType.Ambient);
        await catalog.Locations.AddAsync(location);
        await catalog.SaveChangesAsync();
        return location.Id.Value;
    }

    private async Task<Guid> SeedRecipeAsync(
        HouseholdId household,
        string name,
        int defaultServings,
        params (Guid ProductId, decimal Quantity, Guid UnitId, int Ordinal)[] ingredients)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var recipeId = Guid.NewGuid();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO recipes.recipe
                    (recipe_id, household_id, name, default_servings, created_at, updated_at)
                VALUES
                    (@id, @hid, @name, @servings, NOW(), NOW())
                """;
            cmd.Parameters.AddWithValue("id", recipeId);
            cmd.Parameters.AddWithValue("hid", household.Value);
            cmd.Parameters.AddWithValue("name", name);
            cmd.Parameters.AddWithValue("servings", defaultServings);
            await cmd.ExecuteNonQueryAsync();
        }

        foreach (var (productId, quantity, unitId, ordinal) in ingredients)
        {
            await using var ingCmd = conn.CreateCommand();
            ingCmd.CommandText = """
                INSERT INTO recipes.recipe_ingredient
                    (ingredient_id, household_id, recipe_id, product_id, quantity, unit_id, ordinal)
                VALUES
                    (@id, @hid, @rid, @pid, @qty, @uid, @ord)
                """;
            ingCmd.Parameters.AddWithValue("id", Guid.NewGuid());
            ingCmd.Parameters.AddWithValue("hid", household.Value);
            ingCmd.Parameters.AddWithValue("rid", recipeId);
            ingCmd.Parameters.AddWithValue("pid", productId);
            ingCmd.Parameters.AddWithValue("qty", quantity);
            ingCmd.Parameters.AddWithValue("uid", unitId);
            ingCmd.Parameters.AddWithValue("ord", ordinal);
            await ingCmd.ExecuteNonQueryAsync();
        }

        return recipeId;
    }

    private async Task SeedStockEntryAsync(HouseholdId household, Guid productId, Guid locationId, decimal quantity, Guid unitId)
    {
        // Ensure product_stock root row exists.
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        await using (var ensureCmd = conn.CreateCommand())
        {
            ensureCmd.CommandText = """
                INSERT INTO inventory.product_stock (household_id, product_id, created_at, updated_at)
                VALUES (@hid, @pid, NOW(), NOW())
                ON CONFLICT (household_id, product_id) DO NOTHING
                """;
            ensureCmd.Parameters.AddWithValue("hid", household.Value);
            ensureCmd.Parameters.AddWithValue("pid", productId);
            await ensureCmd.ExecuteNonQueryAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO inventory.stock_entry
                (entry_id, household_id, product_id, location_id, quantity, unit_id, expiry_date,
                 is_open, created_at, updated_at, depleted_at, purchased_at)
            VALUES
                (@id, @hid, @pid, @lid, @qty, @uid, NULL,
                 false, NOW(), NOW(), NULL, NOW())
            """;
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("hid", household.Value);
        cmd.Parameters.AddWithValue("pid", productId);
        cmd.Parameters.AddWithValue("lid", locationId);
        cmd.Parameters.AddWithValue("qty", quantity);
        cmd.Parameters.AddWithValue("uid", unitId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedPriceObservationAsync(HouseholdId household, Guid productId, decimal price, decimal quantity, Guid unitId)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pricing.price_observation
                (observation_id, household_id, product_id, price, quantity, unit_id, unit_price,
                 source, source_ref, observed_at, user_id)
            VALUES
                (@id, @hid, @pid, @price, @qty, @uid, NULL,
                 'Purchase', @ref, NOW(), @usr)
            """;
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("hid", household.Value);
        cmd.Parameters.AddWithValue("pid", productId);
        cmd.Parameters.AddWithValue("price", price);
        cmd.Parameters.AddWithValue("qty", quantity);
        cmd.Parameters.AddWithValue("uid", unitId);
        cmd.Parameters.AddWithValue("ref", Guid.NewGuid());
        cmd.Parameters.AddWithValue("usr", Guid.NewGuid());
        await cmd.ExecuteNonQueryAsync();
    }
}
