using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Plantry.Recipes.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Recipes;

/// <summary>
/// L3 integration tests proving Postgres RLS isolates the Recipes tables — household A physically
/// cannot read household B's recipe rows (PHASE-2-PLAN.md P2-0 done-when; ADR-008). Mirrors
/// <c>StockRlsIsolationTests</c> / <c>ProductRlsIsolationTests</c>.
/// <para>
/// Rows are seeded with raw SQL on the superuser connection rather than a domain factory: the
/// <see cref="Plantry.Recipes.Domain.Recipe"/> aggregate maps only its persistable shape in this P2-0
/// step (authoring behaviour lands in P2-1), so there is no public constructor. The test's concern is
/// row-level isolation, not domain construction, so a direct INSERT is the right seam.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class RecipeRlsIsolationTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _householdA;
    private HouseholdId _householdB;
    private readonly Guid _recipeA = Guid.CreateVersion7();
    private readonly Guid _recipeB = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _householdA = HouseholdId.New();
        _householdB = HouseholdId.New();

        await SeedRecipeAsync(_householdA, _recipeA, "Household A Stew");
        await SeedRecipeAsync(_householdB, _recipeB, "Household B Curry");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedRecipeAsync(HouseholdId household, Guid recipeId, string name)
    {
        // Superuser connection — RLS never applies, so this is a clean seeding seam regardless of policy.
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO recipes.recipe
                (recipe_id, household_id, name, default_servings, created_at, updated_at)
            VALUES
                (@recipe_id, @household_id, @name, 4, now(), now())
            """;
        cmd.Parameters.AddWithValue("recipe_id", recipeId);
        cmd.Parameters.AddWithValue("household_id", household.Value);
        cmd.Parameters.AddWithValue("name", name);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact(DisplayName = "EF query filter: household A cannot see household B's recipes")]
    public async Task EfFilter_HouseholdA_Cannot_Read_HouseholdB_Recipes()
    {
        await using var recipesDb = NewRecipesDb(_householdA);

        var recipes = await recipesDb.Recipes.ToListAsync();

        Assert.All(recipes, r => Assert.Equal(_householdA, r.HouseholdId));
        Assert.DoesNotContain(recipes, r => r.Id.Value == _recipeB);

        var own = Assert.Single(recipes);
        Assert.Equal(_recipeA, own.Id.Value);
    }

    [Fact(DisplayName = "Postgres RLS backstop: raw SQL with wrong app.household_id returns no recipe rows")]
    public async Task RlsPolicy_RawSql_WithWrongHouseholdId_ReturnsNoRows()
    {
        // Connect as the non-superuser app_user role so the policy actually gates rows (RLS never
        // applies to superusers).
        await using var conn = new NpgsqlConnection(db.AppUserConnectionString);
        await conn.OpenAsync();

        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = $"SET app.household_id = '{_householdA.Value}'";
            await setCmd.ExecuteNonQueryAsync();
        }

        await AssertOnlyHouseholdVisibleAsync(conn, "recipes.recipe", _householdA.Value, _householdB.Value);
    }

    [Fact(DisplayName = "RLS backstop (live path): interceptor arms app.household_id; only own household's recipes visible")]
    public async Task Interceptor_OnAppUserConnection_RlsRestrictsRecipesToHousehold()
    {
        var tenant = new TenantContext();
        tenant.Set(_householdA.Value);

        var opts = BuildRecipesOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenant));
        await using var recipesDb = new RecipesDbContext(opts);

        var recipes = await recipesDb.Recipes.IgnoreQueryFilters().ToListAsync();

        Assert.NotEmpty(recipes);
        Assert.All(recipes, r => Assert.Equal(_householdA, r.HouseholdId));
        Assert.DoesNotContain(recipes, r => r.Id.Value == _recipeB);
    }

    [Fact(DisplayName = "RLS backstop (live path): no tenant context => strict policy returns no recipe rows")]
    public async Task Interceptor_NoTenantContext_StrictPolicy_ReturnsNoRecipeRows()
    {
        var tenant = new TenantContext(); // never set

        var opts = BuildRecipesOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenant));
        await using var recipesDb = new RecipesDbContext(opts);

        var recipes = await recipesDb.Recipes.IgnoreQueryFilters().ToListAsync();

        Assert.Empty(recipes);
    }

    private static async Task AssertOnlyHouseholdVisibleAsync(
        NpgsqlConnection conn, string table, Guid expectedHouseholdId, Guid forbiddenHouseholdId)
    {
        await using var selectCmd = conn.CreateCommand();
        selectCmd.CommandText = $"SELECT household_id FROM {table}";
        await using var reader = await selectCmd.ExecuteReaderAsync();

        var seenIds = new List<Guid>();
        while (await reader.ReadAsync())
            seenIds.Add(reader.GetGuid(0));

        Assert.NotEmpty(seenIds);
        Assert.All(seenIds, id => Assert.Equal(expectedHouseholdId, id));
        Assert.DoesNotContain(seenIds, id => id == forbiddenHouseholdId);
    }

    private DbContextOptions<RecipesDbContext> RecipesOptions() =>
        new DbContextOptionsBuilder<RecipesDbContext>().UseNpgsql(db.ConnectionString).Options;

    private static DbContextOptions<RecipesDbContext> BuildRecipesOptions(string connStr, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<RecipesDbContext>().UseNpgsql(connStr);
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return builder.Options;
    }

    private RecipesDbContext NewRecipesDb(HouseholdId household)
    {
        var ctx = new RecipesDbContext(RecipesOptions());
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }
}
