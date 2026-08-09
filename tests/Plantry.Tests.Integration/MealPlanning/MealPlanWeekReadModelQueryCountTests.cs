using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plantry.Pantry.Domain;
using Plantry.Pantry.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.MealPlanning;
using Xunit;
using CatalogUnit = Plantry.Pantry.Domain.Unit;

namespace Plantry.Tests.Integration.MealPlanning;

/// <summary>
/// Pins ADR-021's flat-query-count guarantee for <see cref="MealPlanWeekReadModel.LoadAsync"/> — a
/// critic finding on plantry-yqse pass 1: the ADR's guardrail ("query count must stay independent of
/// meal/dish/ingredient count — verify with the existing span/query count check on the week page, not
/// just by eye") had no test anywhere for this raw-<c>NpgsqlConnection</c> read model, which has no
/// EF-style <c>DbCommandInterceptor</c> seam (unlike every other query-count-tested reader in this
/// project — <c>QueryCountingInterceptor</c> only instruments EF <c>DbContext</c>s).
///
/// <see cref="MealPlanWeekReadModel"/>'s <c>onCommandExecuting</c> constructor parameter (test-only;
/// always null in production) is the seam this suite exercises: it fires once, with a label, right
/// after every <c>conn.CreateCommand()</c> call in the read model — one call per actual round-trip.
/// This suite runs <see cref="MealPlanWeekReadModel.LoadAsync"/> against a SMALL fixture (1 recipe, 1
/// ingredient, no inclusions) and a LARGE one (5 recipes, 15+ ingredients total, a 3-level inclusion
/// chain, stock, prices, and a conversion — exercising every conditional loader branch) and asserts
/// the executed-command COUNT is identical between the two, not merely below some ceiling — the
/// invariant the ADR actually promises.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class MealPlanWeekReadModelQueryCountTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly IClock Clock = SystemClock.Instance;
    private HouseholdId _household;
    private Guid _gramsId;
    private Guid _kgId;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        await using var catalog = NewCatalogDb();
        var grams = CatalogUnit.Create(_household, "g", "grams", Dimension.Mass, 1m, isBase: true);
        var kg = CatalogUnit.Create(_household, "kg", "kilograms", Dimension.Mass, 1000m);
        await catalog.Units.AddRangeAsync(grams, kg);
        await catalog.SaveChangesAsync();
        _gramsId = grams.Id.Value;
        _kgId = kg.Id.Value;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "plantry-yqse: LoadAsync issues the SAME query count for a small week and a large one with a multi-level inclusion chain")]
    public async Task LoadAsync_QueryCount_IsIndependent_OfPageSize()
    {
        // ── Small fixture: 1 recipe, 1 ingredient, no inclusions, no stock/price/conversions. ──
        var smallProductId = await SeedProductAsync("Small Product");
        var smallRecipeId = await SeedRecipeAsync("Small Recipe", 1, (smallProductId, 1m, _gramsId, 1));

        var smallLog = new List<string>();
        var smallRm = NewReadModel(cmd => smallLog.Add(cmd));
        await smallRm.LoadAsync([smallRecipeId], []);

        // ── Large fixture: 5 recipes (≥15 ingredients total), a 3-level inclusion chain, stock, a
        //    price, and a conversion — every conditional loader branch gets exercised. ──
        var largeProductIds = new List<Guid>();
        for (var i = 0; i < 15; i++)
            largeProductIds.Add(await SeedProductAsync($"Large Product {i}"));

        var largeRecipeIds = new List<Guid>();
        for (var r = 0; r < 5; r++)
        {
            var ingredients = Enumerable.Range(0, 3)
                .Select(i => (largeProductIds[r * 3 + i], 10m, _gramsId, i + 1))
                .ToArray();
            largeRecipeIds.Add(await SeedRecipeAsync($"Large Recipe {r}", 2, ingredients));
        }

        // 3-level inclusion chain among the large recipes: recipe0 includes recipe1 includes recipe2.
        await SeedInclusionAsync(largeRecipeIds[0], largeRecipeIds[1], servings: 1m, ordinal: 4);
        await SeedInclusionAsync(largeRecipeIds[1], largeRecipeIds[2], servings: 1m, ordinal: 4);

        // Stock, price, and a conversion on one product — exercises LoadStockAsync,
        // LoadLatestPricesAsync (both legs still run regardless of rows found), and LoadConversionsAsync.
        var locationId = await SeedLocationAsync("Pantry");
        await SeedStockEntryAsync(largeProductIds[0], locationId, 500m, _gramsId);
        await SeedPriceObservationAsync(largeProductIds[0], 2.00m, 500m, _gramsId);
        await SeedConversionAsync(largeProductIds[0], _kgId, _gramsId, 1000m);

        var largeLog = new List<string>();
        var largeRm = NewReadModel(cmd => largeLog.Add(cmd));
        await largeRm.LoadAsync(largeRecipeIds, []);

        Assert.Equal(smallLog.Count, largeLog.Count);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private MealPlanWeekReadModel NewReadModel(Action<string> onCommandExecuting)
    {
        var tenant = new TenantContext();
        tenant.Set(_household.Value);
        return new MealPlanWeekReadModel(db.ConnectionString, tenant, Clock, onCommandExecuting);
    }

    private PantryDbContext NewCatalogDb()
    {
        var opts = new DbContextOptionsBuilder<PantryDbContext>().UseNpgsql(db.ConnectionString).Options;
        var ctx = new PantryDbContext(opts);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private async Task<Guid> SeedProductAsync(string name)
    {
        await using var catalog = NewCatalogDb();
        var unitId = UnitId.From(_gramsId);
        var product = Product.Create(_household, name, unitId, Clock, trackStock: true);
        await catalog.Products.AddAsync(product);
        await catalog.SaveChangesAsync();
        return product.Id.Value;
    }

    private async Task<Guid> SeedLocationAsync(string name)
    {
        await using var catalog = NewCatalogDb();
        var location = Location.Create(_household, name, LocationType.Ambient);
        await catalog.Locations.AddAsync(location);
        await catalog.SaveChangesAsync();
        return location.Id.Value;
    }

    /// <summary>Seeds a recipe with the given ingredients into recipes.recipe + recipe_ingredient (raw SQL).</summary>
    private async Task<Guid> SeedRecipeAsync(
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
            cmd.Parameters.AddWithValue("hid", _household.Value);
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
            ingCmd.Parameters.AddWithValue("hid", _household.Value);
            ingCmd.Parameters.AddWithValue("rid", recipeId);
            ingCmd.Parameters.AddWithValue("pid", productId);
            ingCmd.Parameters.AddWithValue("qty", quantity);
            ingCmd.Parameters.AddWithValue("uid", unitId);
            ingCmd.Parameters.AddWithValue("ord", ordinal);
            await ingCmd.ExecuteNonQueryAsync();
        }

        return recipeId;
    }

    private async Task SeedInclusionAsync(Guid recipeId, Guid subRecipeId, decimal servings, int ordinal)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO recipes.recipe_inclusion
                (inclusion_id, household_id, recipe_id, sub_recipe_id, servings, group_heading, ordinal)
            VALUES
                (@id, @hid, @rid, @sid, @servings, NULL, @ord)
            """;
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("hid", _household.Value);
        cmd.Parameters.AddWithValue("rid", recipeId);
        cmd.Parameters.AddWithValue("sid", subRecipeId);
        cmd.Parameters.AddWithValue("servings", servings);
        cmd.Parameters.AddWithValue("ord", ordinal);
        await cmd.ExecuteNonQueryAsync();
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

    private async Task SeedStockEntryAsync(Guid productId, Guid locationId, decimal quantity, Guid unitId)
    {
        await EnsureProductStockAsync(productId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
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
        cmd.Parameters.AddWithValue("hid", _household.Value);
        cmd.Parameters.AddWithValue("pid", productId);
        cmd.Parameters.AddWithValue("lid", locationId);
        cmd.Parameters.AddWithValue("qty", quantity);
        cmd.Parameters.AddWithValue("uid", unitId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedPriceObservationAsync(Guid productId, decimal price, decimal quantity, Guid unitId)
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
        cmd.Parameters.AddWithValue("hid", _household.Value);
        cmd.Parameters.AddWithValue("pid", productId);
        cmd.Parameters.AddWithValue("price", price);
        cmd.Parameters.AddWithValue("qty", quantity);
        cmd.Parameters.AddWithValue("uid", unitId);
        cmd.Parameters.AddWithValue("ref", Guid.NewGuid());
        cmd.Parameters.AddWithValue("usr", Guid.NewGuid());
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedConversionAsync(Guid productId, Guid fromUnitId, Guid toUnitId, decimal factor)
    {
        await using var catalog = NewCatalogDb();
        await catalog.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO catalog.product_conversions
                (id, household_id, product_id, from_unit_id, to_unit_id, factor)
            VALUES
                ({0}, {1}, {2}, {3}, {4}, {5})
            """,
            Guid.NewGuid(), _household.Value, productId, fromUnitId, toUnitId, factor);
    }
}
