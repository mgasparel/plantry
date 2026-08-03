using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Recipes;

/// <summary>
/// L3 integration tests for the <see cref="RecipeRating"/> table (plantry-zlwp.1). Covers: schema
/// round-trip via the repository, the UNIQUE (household_id, recipe_id, user_id) constraint, the
/// stars CHECK constraint, EF query filter household isolation, and the RLS backstop (raw SQL +
/// live interceptor path) — mirrors <c>UserPreferenceTests</c> / <c>RecipeRlsIsolationTests</c>.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class RecipeRatingTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _householdA;
    private HouseholdId _householdB;
    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly IClock Clock = SystemClock.Instance;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _householdA = HouseholdId.New();
        _householdB = HouseholdId.New();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Schema round-trip ──────────────────────────────────────────────────────

    [Fact(DisplayName = "RecipeRating round-trips through the repository")]
    public async Task RoundTrip_RecipeRating()
    {
        var recipeId = await SeedRecipeAsync(_householdA, "Household A Stew");

        await using (var writeDb = NewRecipesDb(_householdA))
        {
            var repo = new RecipeRatingRepository(writeDb);
            var rating = RecipeRating.Create(_householdA, recipeId, UserA, 4, Clock);
            await repo.AddAsync(rating);
            await repo.SaveChangesAsync();
        }

        await using var readDb = NewRecipesDb(_householdA);
        var readRepo = new RecipeRatingRepository(readDb);
        var loaded = await readRepo.FindAsync(recipeId, UserA);

        Assert.NotNull(loaded);
        Assert.Equal(_householdA, loaded.HouseholdId);
        Assert.Equal(recipeId, loaded.RecipeId);
        Assert.Equal(UserA, loaded.UserId);
        Assert.Equal(4, loaded.Stars);
    }

    [Fact(DisplayName = "Clearing a rating (repository Remove) deletes the row — no opinion is absence of a row")]
    public async Task Remove_Deletes_The_Row()
    {
        var recipeId = await SeedRecipeAsync(_householdA, "Household A Stew");

        await using (var writeDb = NewRecipesDb(_householdA))
        {
            var repo = new RecipeRatingRepository(writeDb);
            var rating = RecipeRating.Create(_householdA, recipeId, UserA, 4, Clock);
            await repo.AddAsync(rating);
            await repo.SaveChangesAsync();
        }

        await using (var clearDb = NewRecipesDb(_householdA))
        {
            var repo = new RecipeRatingRepository(clearDb);
            var existing = await repo.FindAsync(recipeId, UserA);
            Assert.NotNull(existing);
            repo.Remove(existing);
            await repo.SaveChangesAsync();
        }

        await using var readDb = NewRecipesDb(_householdA);
        var readRepo = new RecipeRatingRepository(readDb);
        Assert.Null(await readRepo.FindAsync(recipeId, UserA));
    }

    [Fact(DisplayName = "UNIQUE (household_id, recipe_id, user_id) — duplicate rating for the same member throws")]
    public async Task UniqueConstraint_ThrowsOnDuplicate()
    {
        var recipeId = await SeedRecipeAsync(_householdA, "Household A Stew");

        await using var writeDb = NewRecipesDb(_householdA);
        var repo = new RecipeRatingRepository(writeDb);
        var rating = RecipeRating.Create(_householdA, recipeId, UserA, 3, Clock);
        await repo.AddAsync(rating);
        await repo.SaveChangesAsync();

        // Insert a second row for the same (household, recipe, user) via raw SQL, bypassing the
        // domain's own upsert discipline — the DB constraint must reject it regardless.
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO recipes.recipe_rating (recipe_rating_id, household_id, recipe_id, user_id, stars, created_at, updated_at)
            VALUES (gen_random_uuid(), '{_householdA.Value}', '{recipeId.Value}', '{UserA}', 5, now(), now())
            """;

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23505", ex.SqlState); // unique_violation
    }

    [Fact(DisplayName = "stars CHECK constraint rejects values outside 1..5")]
    public async Task Check_Stars_RejectsInvalid()
    {
        var recipeId = await SeedRecipeAsync(_householdA, "Household A Stew");

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO recipes.recipe_rating (recipe_rating_id, household_id, recipe_id, user_id, stars, created_at, updated_at)
            VALUES (gen_random_uuid(), '{_householdA.Value}', '{recipeId.Value}', '{Guid.NewGuid()}', 6, now(), now())
            """;

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23514", ex.SqlState); // check_violation
    }

    [Fact(DisplayName = "Composite FK: recipe_rating.household_id must match recipe.household_id")]
    public async Task CompositeFk_HouseholdMustMatch()
    {
        var recipeIdA = await SeedRecipeAsync(_householdA, "Household A Stew");

        // Attempt to insert a rating with household B's id but pointing at household A's recipe —
        // the composite FK (household_id, recipe_id) references recipe should reject this.
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO recipes.recipe_rating (recipe_rating_id, household_id, recipe_id, user_id, stars, created_at, updated_at)
            VALUES (gen_random_uuid(), '{_householdB.Value}', '{recipeIdA.Value}', '{Guid.NewGuid()}', 3, now(), now())
            """;

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23503", ex.SqlState); // foreign_key_violation
    }

    [Fact(DisplayName = "EF query filter: household A cannot see household B's ratings")]
    public async Task EfFilter_HouseholdA_Cannot_Read_HouseholdB_Ratings()
    {
        var recipeIdB = await SeedRecipeAsync(_householdB, "Household B Curry");

        await using (var seedDb = NewRecipesDb(_householdB))
        {
            var repo = new RecipeRatingRepository(seedDb);
            await repo.AddAsync(RecipeRating.Create(_householdB, recipeIdB, UserA, 5, Clock));
            await repo.SaveChangesAsync();
        }

        await using var readAsA = NewRecipesDb(_householdA);
        var found = await readAsA.RecipeRatings.ToListAsync();

        Assert.Empty(found);
    }

    [Fact(DisplayName = "RLS backstop: raw SQL with wrong household returns no recipe_rating rows")]
    public async Task RlsPolicy_RawSql_WrongHousehold_ReturnsNoRows()
    {
        var recipeIdB = await SeedRecipeAsync(_householdB, "Household B Curry");

        await using (var seedDb = NewRecipesDb(_householdB))
        {
            var repo = new RecipeRatingRepository(seedDb);
            await repo.AddAsync(RecipeRating.Create(_householdB, recipeIdB, UserA, 5, Clock));
            await repo.SaveChangesAsync();
        }

        await using var conn = new NpgsqlConnection(db.AppUserConnectionString);
        await conn.OpenAsync();

        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = $"SET app.household_id = '{_householdA.Value}'";
            await setCmd.ExecuteNonQueryAsync();
        }

        await using var select = conn.CreateCommand();
        select.CommandText = "SELECT recipe_rating_id FROM recipes.recipe_rating";
        await using var reader = await select.ExecuteReaderAsync();

        var ids = new List<Guid>();
        while (await reader.ReadAsync())
            ids.Add(reader.GetGuid(0));

        Assert.Empty(ids);
    }

    [Fact(DisplayName = "RLS backstop (live path): interceptor arms app.household_id; only own household's ratings visible")]
    public async Task Interceptor_OnAppUserConnection_RlsRestrictsRatingsToHousehold()
    {
        var recipeIdA = await SeedRecipeAsync(_householdA, "Household A Stew");
        var recipeIdB = await SeedRecipeAsync(_householdB, "Household B Curry");

        await using (var seedDb = NewRecipesDb(_householdA))
        {
            var repo = new RecipeRatingRepository(seedDb);
            await repo.AddAsync(RecipeRating.Create(_householdA, recipeIdA, UserA, 4, Clock));
            await repo.SaveChangesAsync();
        }
        await using (var seedDb = NewRecipesDb(_householdB))
        {
            var repo = new RecipeRatingRepository(seedDb);
            await repo.AddAsync(RecipeRating.Create(_householdB, recipeIdB, UserA, 2, Clock));
            await repo.SaveChangesAsync();
        }

        var tenant = new TenantContext();
        tenant.Set(_householdA.Value);

        var opts = BuildRecipesOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenant));
        await using var recipesDb = new RecipesDbContext(opts);

        var ratings = await recipesDb.RecipeRatings.IgnoreQueryFilters().ToListAsync();

        Assert.NotEmpty(ratings);
        Assert.All(ratings, r => Assert.Equal(_householdA, r.HouseholdId));
        Assert.DoesNotContain(ratings, r => r.RecipeId == recipeIdB);
    }

    [Fact(DisplayName = "RLS backstop (live path): no tenant context => strict policy returns no recipe_rating rows")]
    public async Task Interceptor_NoTenantContext_StrictPolicy_ReturnsNoRatingRows()
    {
        var recipeIdA = await SeedRecipeAsync(_householdA, "Household A Stew");
        await using (var seedDb = NewRecipesDb(_householdA))
        {
            var repo = new RecipeRatingRepository(seedDb);
            await repo.AddAsync(RecipeRating.Create(_householdA, recipeIdA, UserA, 4, Clock));
            await repo.SaveChangesAsync();
        }

        var tenant = new TenantContext(); // never set

        var opts = BuildRecipesOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenant));
        await using var recipesDb = new RecipesDbContext(opts);

        var ratings = await recipesDb.RecipeRatings.IgnoreQueryFilters().ToListAsync();

        Assert.Empty(ratings);
    }

    // ── ListByRecipeAsync / ListByRecipeIdsAsync (plantry-zlwp.1 pass-2 FIX) ───

    [Fact(DisplayName = "ListByRecipeIdsAsync groups rows by recipe and returns only the requested ids")]
    public async Task ListByRecipeIdsAsync_Returns_Rows_Grouped_By_Recipe()
    {
        var recipe1 = await SeedRecipeAsync(_householdA, "Household A Stew");
        var recipe2 = await SeedRecipeAsync(_householdA, "Household A Chili");
        var userB = Guid.NewGuid();

        await using (var seedDb = NewRecipesDb(_householdA))
        {
            var repo = new RecipeRatingRepository(seedDb);
            await repo.AddAsync(RecipeRating.Create(_householdA, recipe1, UserA, 4, Clock));
            await repo.AddAsync(RecipeRating.Create(_householdA, recipe2, userB, 2, Clock));
            await repo.SaveChangesAsync();
        }

        await using var readDb = NewRecipesDb(_householdA);
        var readRepo = new RecipeRatingRepository(readDb);

        var both = await readRepo.ListByRecipeIdsAsync([recipe1, recipe2]);
        Assert.Equal(2, both.Count);
        Assert.Contains(both, r => r.RecipeId == recipe1 && r.UserId == UserA && r.Stars == 4);
        Assert.Contains(both, r => r.RecipeId == recipe2 && r.UserId == userB && r.Stars == 2);

        var onlyFirst = await readRepo.ListByRecipeIdsAsync([recipe1]);
        var only = Assert.Single(onlyFirst);
        Assert.Equal(recipe1, only.RecipeId);
    }

    [Fact(DisplayName = "ListByRecipeIdsAsync with an empty list returns an empty result")]
    public async Task ListByRecipeIdsAsync_EmptyList_Returns_Empty()
    {
        await using var readDb = NewRecipesDb(_householdA);
        var readRepo = new RecipeRatingRepository(readDb);

        var result = await readRepo.ListByRecipeIdsAsync([]);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "ListByRecipeAsync returns only the requested recipe's rows, excluding another recipe's ratings")]
    public async Task ListByRecipeAsync_Returns_Only_Requested_Recipes_Rows()
    {
        var rated = await SeedRecipeAsync(_householdA, "Household A Stew");
        var other = await SeedRecipeAsync(_householdA, "Household A Chili");

        await using (var seedDb = NewRecipesDb(_householdA))
        {
            var repo = new RecipeRatingRepository(seedDb);
            await repo.AddAsync(RecipeRating.Create(_householdA, rated, UserA, 5, Clock));
            await repo.AddAsync(RecipeRating.Create(_householdA, other, Guid.NewGuid(), 3, Clock));
            await repo.SaveChangesAsync();
        }

        await using var readDb = NewRecipesDb(_householdA);
        var readRepo = new RecipeRatingRepository(readDb);

        var rows = await readRepo.ListByRecipeAsync(rated);

        var row = Assert.Single(rows);
        Assert.Equal(rated, row.RecipeId);
        Assert.Equal(UserA, row.UserId);
        Assert.Equal(5, row.Stars);
    }

    [Fact(DisplayName = "ListByRecipeAsync/ListByRecipeIdsAsync: household A cannot see household B's ratings via either method")]
    public async Task ListMethods_HouseholdA_Cannot_See_HouseholdB_Ratings()
    {
        var recipeIdB = await SeedRecipeAsync(_householdB, "Household B Curry");

        await using (var seedDb = NewRecipesDb(_householdB))
        {
            var repo = new RecipeRatingRepository(seedDb);
            await repo.AddAsync(RecipeRating.Create(_householdB, recipeIdB, UserA, 5, Clock));
            await repo.SaveChangesAsync();
        }

        await using var readAsA = NewRecipesDb(_householdA);
        var readRepo = new RecipeRatingRepository(readAsA);

        Assert.Empty(await readRepo.ListByRecipeAsync(recipeIdB));
        Assert.Empty(await readRepo.ListByRecipeIdsAsync([recipeIdB]));
    }

    // ── Seed helper ────────────────────────────────────────────────────────────

    private async Task<RecipeId> SeedRecipeAsync(HouseholdId household, string name)
    {
        var recipeId = RecipeId.New();

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO recipes.recipe
                (recipe_id, household_id, name, default_servings, created_at, updated_at)
            VALUES
                (@recipe_id, @household_id, @name, 4, now(), now())
            """;
        cmd.Parameters.AddWithValue("recipe_id", recipeId.Value);
        cmd.Parameters.AddWithValue("household_id", household.Value);
        cmd.Parameters.AddWithValue("name", name);
        await cmd.ExecuteNonQueryAsync();

        return recipeId;
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
