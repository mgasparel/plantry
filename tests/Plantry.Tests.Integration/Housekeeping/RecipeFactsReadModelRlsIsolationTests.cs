using Npgsql;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.Housekeeping;
using Xunit;

namespace Plantry.Tests.Integration.Housekeeping;

/// <summary>
/// L3 RLS isolation test for <see cref="RecipeFactsReadModel"/> (ADR-021 rule 4): two households, one
/// read model, no leakage — proves the household-wide recipe load (no caller-supplied id set to narrow
/// by) is still Postgres-RLS-isolated on the raw <c>recipes.recipe</c>/<c>recipes.recipe_ingredient</c>
/// tables it scans without a WHERE household_id clause of its own.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class RecipeFactsReadModelRlsIsolationTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _householdA;
    private HouseholdId _householdB;
    private Guid _recipeA;
    private Guid _recipeB;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _householdA = HouseholdId.From(Guid.Parse("00000000-0000-0000-0000-000000000041"));
        _householdB = HouseholdId.From(Guid.Parse("00000000-0000-0000-0000-000000000042"));


        _recipeA = await SeedRecipeAsync(_householdA, "Household A Recipe");
        _recipeB = await SeedRecipeAsync(_householdB, "Household B Recipe");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "Household A's read model never sees household B's recipes")]
    public async Task LoadAsync_HouseholdA_DoesNotSee_HouseholdB()
    {
        var tenant = new TenantContext();
        tenant.Set(_householdA.Value);
        var rm = new RecipeFactsReadModel(db.AppUserConnectionString, tenant);

        var bag = await rm.LoadAsync();

        Assert.Contains(_recipeA, bag.Recipes.Keys);
        Assert.DoesNotContain(_recipeB, bag.Recipes.Keys);
    }

    [Fact(DisplayName = "No tenant set — the RLS-armed connection returns no recipe rows")]
    public async Task LoadAsync_NoTenant_ReturnsNoRecipes()
    {
        var tenant = new TenantContext(); // never set
        var rm = new RecipeFactsReadModel(db.AppUserConnectionString, tenant);

        var bag = await rm.LoadAsync();

        Assert.Empty(bag.Recipes);
    }

    private async Task<Guid> SeedRecipeAsync(HouseholdId household, string name)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var recipeId = household == _householdA ? Guid.Parse("00000000-0000-0000-0000-000000000043") : Guid.Parse("00000000-0000-0000-0000-000000000044");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO recipes.recipe
                (recipe_id, household_id, name, default_servings, created_at, updated_at)
            VALUES
                (@id, @hid, @name, 4, NOW(), NOW())
            """;
        cmd.Parameters.AddWithValue("id", recipeId);
        cmd.Parameters.AddWithValue("hid", household.Value);
        cmd.Parameters.AddWithValue("name", name);
        await cmd.ExecuteNonQueryAsync();
        return recipeId;
    }
}
