using Microsoft.EntityFrameworkCore;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Recipes;

/// <summary>
/// L3 integration tests proving <see cref="CookEvent"/> persists and reloads through EF
/// against a real Postgres schema (P2-3a acceptance criteria — persist + reload via repository).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class CookEventRepositoryTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private RecipeId _recipeId;
    private readonly IClock _clock = SystemClock.Instance;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        // Seed a recipe row so the cook_event FK (household_id, recipe_id) is satisfiable.
        await using var ctx = NewContext();
        var repo = new RecipeRepository(ctx);
        var recipe = Recipe.Create(_household, "Test Recipe For Cook", 2, _clock).Value;
        recipe.ReplaceIngredients(
        [new IngredientLine(Guid.CreateVersion7(), 1m, Guid.CreateVersion7(), null, 0)], _clock);
        _recipeId = recipe.Id;
        await repo.AddAsync(recipe);
        await repo.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Round-trip ────────────────────────────────────────────────────────────

    [Fact(DisplayName = "CookEvent persists and reloads with all fields intact")]
    public async Task CookEvent_RoundTrips_All_Fields()
    {
        var userId = Guid.CreateVersion7();
        var fixedNow = new DateTimeOffset(2026, 6, 14, 10, 0, 0, TimeSpan.Zero);
        var fixedClock = new FixedClock(fixedNow);

        CookEventId cookEventId;

        await using (var ctx = NewContext())
        {
            var repo = new CookEventRepository(ctx);
            var cookEvent = CookEvent.Record(_recipeId, _household, servingsCooked: 3, userId, fixedClock).Value;
            cookEventId = cookEvent.Id;
            await repo.AddAsync(cookEvent);
            await repo.SaveChangesAsync();
        }

        await using var ctx2 = NewContext();
        var events = await new CookEventRepository(ctx2).ListByRecipeAsync(_recipeId);

        Assert.Single(events);
        var loaded = events[0];
        Assert.Equal(cookEventId, loaded.Id);
        Assert.Equal(_household, loaded.HouseholdId);
        Assert.Equal(_recipeId, loaded.RecipeId);
        Assert.Equal(3, loaded.ServingsCooked);
        Assert.Equal(userId, loaded.CookedBy);
        Assert.Equal(fixedNow, loaded.CookedAt);
    }

    [Fact(DisplayName = "ListByRecipeAsync returns events ordered by cooked_at descending")]
    public async Task ListByRecipeAsync_Orders_By_CookedAt_Descending()
    {
        var earlier = new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 6, 14, 10, 0, 0, TimeSpan.Zero);
        var userId = Guid.CreateVersion7();

        await using (var ctx = NewContext())
        {
            var repo = new CookEventRepository(ctx);
            var first = CookEvent.Record(_recipeId, _household, 2, userId, new FixedClock(earlier)).Value;
            var second = CookEvent.Record(_recipeId, _household, 4, userId, new FixedClock(later)).Value;
            await repo.AddAsync(first);
            await repo.AddAsync(second);
            await repo.SaveChangesAsync();
        }

        await using var ctx2 = NewContext();
        var events = await new CookEventRepository(ctx2).ListByRecipeAsync(_recipeId);

        Assert.Equal(2, events.Count);
        // Most recent first
        Assert.Equal(later, events[0].CookedAt);
        Assert.Equal(earlier, events[1].CookedAt);
    }

    [Fact(DisplayName = "ListByRecipeAsync returns empty list when no events exist")]
    public async Task ListByRecipeAsync_Returns_Empty_For_Unknown_Recipe()
    {
        await using var ctx = NewContext();
        var events = await new CookEventRepository(ctx).ListByRecipeAsync(RecipeId.New());

        Assert.Empty(events);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private RecipesDbContext NewContext()
    {
        var opts = new DbContextOptionsBuilder<RecipesDbContext>()
            .UseNpgsql(db.ConnectionString)
            .Options;
        var ctx = new RecipesDbContext(opts);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
