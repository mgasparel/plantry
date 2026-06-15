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
/// L3 integration tests proving <see cref="CookConsumeLine"/> persists, reloads, and mutates
/// correctly against the real Postgres schema (plantry-292b acceptance criteria):
/// <list type="bullet">
/// <item>Lines round-trip all fields through EF with correct Status/Shortfall/CookEventId.</item>
/// <item>Status transitions (Pending → Applied / Shorted) survive a SaveChanges + reload.</item>
/// <item>The <c>ck_cook_consume_line_status</c> CHECK constraint rejects unknown status values.</item>
/// <item>Postgres RLS on <c>recipes.cook_consume_line</c> isolates household A from household B.</item>
/// </list>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class CookConsumeLineRepositoryTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private RecipeId _recipeId;
    private readonly IClock _clock = SystemClock.Instance;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        // Seed a recipe so cook_event FK (household_id, recipe_id) is satisfiable.
        await using var ctx = NewContext(_household);
        var repo = new RecipeRepository(ctx);
        var recipe = Recipe.Create(_household, "Integration Test Recipe", 2, _clock).Value;
        recipe.ReplaceIngredients(
            [new IngredientLine(Guid.CreateVersion7(), 1m, Guid.CreateVersion7(), null, 0)],
            _clock);
        _recipeId = recipe.Id;
        await repo.AddAsync(recipe);
        await repo.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Round-trip: all fields survive a SaveChanges + reload ────────────────────

    [Fact(DisplayName = "CookConsumeLine fields round-trip through EF intact")]
    public async Task CookConsumeLine_RoundTrips_All_Fields()
    {
        var userId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();
        var ingredientId = Guid.CreateVersion7();
        const decimal quantity = 250m;

        CookEventId cookEventId;
        CookConsumeLineId lineId;

        await using (var ctx = NewContext(_household))
        {
            var cookEvent = CookEvent.Record(_recipeId, _household, servingsCooked: 2, userId, _clock).Value;
            cookEventId = cookEvent.Id;
            var line = cookEvent.AddConsumeLine(ingredientId, productId, quantity, unitId);
            lineId = line.Id;

            await ctx.CookEvents.AddAsync(cookEvent);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewContext(_household);
        var loaded = await ctx2.CookEvents
            .Include(e => e.ConsumeLines)
            .SingleAsync(e => e.Id == cookEventId);

        var loadedLine = Assert.Single(loaded.ConsumeLines);
        Assert.Equal(lineId, loadedLine.Id);
        Assert.Equal(cookEventId, loadedLine.CookEventId);
        Assert.Equal(_household, loadedLine.HouseholdId);
        Assert.Equal(ingredientId, loadedLine.IngredientId);
        Assert.Equal(productId, loadedLine.ProductId);
        Assert.Equal(quantity, loadedLine.Quantity);
        Assert.Equal(unitId, loadedLine.UnitId);
        Assert.Equal(CookConsumeLineStatus.Pending, loadedLine.Status);
        Assert.Equal(0m, loadedLine.Shortfall);
    }

    // ── Status transition: Pending → Applied ─────────────────────────────────────

    [Fact(DisplayName = "MarkApplied persists Applied status and shortfall after second SaveChanges")]
    public async Task MarkApplied_Persists_Applied_Status_And_Shortfall()
    {
        var userId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();
        const decimal quantity = 100m;
        const decimal shortfall = 30m;

        CookEventId cookEventId;

        // First commit: anchor (Pending lines).
        await using (var ctx = NewContext(_household))
        {
            var cookEvent = CookEvent.Record(_recipeId, _household, servingsCooked: 2, userId, _clock).Value;
            cookEventId = cookEvent.Id;
            cookEvent.AddConsumeLine(Guid.CreateVersion7(), productId, quantity, unitId);
            await ctx.CookEvents.AddAsync(cookEvent);
            await ctx.SaveChangesAsync();
        }

        // Second commit: mark line Applied.
        await using (var ctx = NewContext(_household))
        {
            var cookEvent = await ctx.CookEvents
                .Include(e => e.ConsumeLines)
                .SingleAsync(e => e.Id == cookEventId);

            var line = Assert.Single(cookEvent.ConsumeLines);
            Assert.Equal(CookConsumeLineStatus.Pending, line.Status);

            line.MarkApplied(shortfall);
            await ctx.SaveChangesAsync();
        }

        // Reload: assert Applied + shortfall.
        await using var ctx2 = NewContext(_household);
        var loaded = await ctx2.CookEvents
            .Include(e => e.ConsumeLines)
            .SingleAsync(e => e.Id == cookEventId);

        var loadedLine = Assert.Single(loaded.ConsumeLines);
        Assert.Equal(CookConsumeLineStatus.Applied, loadedLine.Status);
        Assert.Equal(shortfall, loadedLine.Shortfall);
    }

    // ── Status transition: Pending → Shorted ─────────────────────────────────────

    [Fact(DisplayName = "MarkShorted persists Shorted status with full quantity as shortfall")]
    public async Task MarkShorted_Persists_Shorted_Status_With_Full_Quantity()
    {
        var userId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();
        const decimal quantity = 150m;

        CookEventId cookEventId;

        await using (var ctx = NewContext(_household))
        {
            var cookEvent = CookEvent.Record(_recipeId, _household, servingsCooked: 4, userId, _clock).Value;
            cookEventId = cookEvent.Id;
            cookEvent.AddConsumeLine(Guid.CreateVersion7(), productId, quantity, unitId);
            await ctx.CookEvents.AddAsync(cookEvent);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = NewContext(_household))
        {
            var cookEvent = await ctx.CookEvents
                .Include(e => e.ConsumeLines)
                .SingleAsync(e => e.Id == cookEventId);

            Assert.Single(cookEvent.ConsumeLines).MarkShorted();
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewContext(_household);
        var loaded = await ctx2.CookEvents
            .Include(e => e.ConsumeLines)
            .SingleAsync(e => e.Id == cookEventId);

        var loadedLine = Assert.Single(loaded.ConsumeLines);
        Assert.Equal(CookConsumeLineStatus.Shorted, loadedLine.Status);
        Assert.Equal(quantity, loadedLine.Shortfall); // MarkShorted sets Shortfall = Quantity
    }

    // ── CHECK constraint: invalid status value is rejected by Postgres ──────────

    [Fact(DisplayName = "CHECK constraint rejects an invalid status value via raw SQL")]
    public async Task CheckConstraint_Rejects_Invalid_Status_Value()
    {
        // Seed a valid cook_event to satisfy the FK.
        var cookEventId = Guid.CreateVersion7();
        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO recipes.cook_event
                    (cook_event_id, household_id, recipe_id, servings_cooked, cooked_by, cooked_at)
                VALUES
                    (@id, @hid, @rid, 2, @uid, now())
                """;
            cmd.Parameters.AddWithValue("id", cookEventId);
            cmd.Parameters.AddWithValue("hid", _household.Value);
            cmd.Parameters.AddWithValue("rid", _recipeId.Value);
            cmd.Parameters.AddWithValue("uid", Guid.CreateVersion7());
            await cmd.ExecuteNonQueryAsync();
        }

        // Attempt to insert a line with an invalid status value.
        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO recipes.cook_consume_line
                    (cook_consume_line_id, household_id, cook_event_id, ingredient_id,
                     product_id, quantity, unit_id, status, shortfall)
                VALUES
                    (@id, @hid, @ceid, @iid, @pid, 100, @uid, 'INVALID_STATUS', 0)
                """;
            cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
            cmd.Parameters.AddWithValue("hid", _household.Value);
            cmd.Parameters.AddWithValue("ceid", cookEventId);
            cmd.Parameters.AddWithValue("iid", Guid.CreateVersion7());
            cmd.Parameters.AddWithValue("pid", Guid.CreateVersion7());
            cmd.Parameters.AddWithValue("uid", Guid.CreateVersion7());

            var ex = await Assert.ThrowsAsync<PostgresException>(
                () => cmd.ExecuteNonQueryAsync());

            // Postgres raises 23514 (check_violation) for CHECK constraint failures.
            Assert.Equal("23514", ex.SqlState);
            Assert.Contains("ck_cook_consume_line_status", ex.ConstraintName ?? string.Empty);
        }
    }

    // ── RLS isolation: household A cannot see household B's cook_consume_line rows ─

    [Fact(DisplayName = "RLS: household A cannot read household B's cook_consume_line rows via app_user")]
    public async Task RlsPolicy_HouseholdA_Cannot_Read_HouseholdB_CookConsumeLines()
    {
        var householdB = HouseholdId.New();

        // Seed a recipe for household B.
        await using (var ctx = NewContext(householdB))
        {
            var repo = new RecipeRepository(ctx);
            var recipe = Recipe.Create(householdB, "HH-B Recipe", 2, _clock).Value;
            recipe.ReplaceIngredients(
                [new IngredientLine(Guid.CreateVersion7(), 1m, Guid.CreateVersion7(), null, 0)],
                _clock);
            await repo.AddAsync(recipe);
            await repo.SaveChangesAsync();

            var recipeId = recipe.Id;
            var userId = Guid.CreateVersion7();
            var cookEvent = CookEvent.Record(recipeId, householdB, servingsCooked: 2, userId, _clock).Value;
            cookEvent.AddConsumeLine(Guid.CreateVersion7(), Guid.CreateVersion7(), 100m, Guid.CreateVersion7());
            await ctx.CookEvents.AddAsync(cookEvent);
            await ctx.SaveChangesAsync();
        }

        // Assert household A sees NO lines from household B via the app_user path.
        var tenant = new TenantContext();
        tenant.Set(_household.Value);
        var opts = BuildRecipesOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenant));

        await using var appCtx = new RecipesDbContext(opts);
        // IgnoreQueryFilters so the test exercises RLS alone (not the EF filter double).
        var lines = await appCtx.CookConsumeLines.IgnoreQueryFilters().ToListAsync();

        // Household A has not cooked anything in this test run, so it should see no lines.
        Assert.Empty(lines);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private RecipesDbContext NewContext(HouseholdId household)
    {
        var opts = new DbContextOptionsBuilder<RecipesDbContext>()
            .UseNpgsql(db.ConnectionString)
            .Options;
        var ctx = new RecipesDbContext(opts);
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }

    private static DbContextOptions<RecipesDbContext> BuildRecipesOptions(
        string connStr, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<RecipesDbContext>().UseNpgsql(connStr);
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return builder.Options;
    }
}
