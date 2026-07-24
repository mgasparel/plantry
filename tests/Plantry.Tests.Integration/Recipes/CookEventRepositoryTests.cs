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

    // ── DeferredUnitGap persistence + query (plantry-qll2.6) ────────────────────

    [Fact(DisplayName = "DeferredUnitGap line persists (CHECK admits it) and reloads with its status")]
    public async Task DeferredUnitGap_Line_Persists_And_Reloads()
    {
        var userId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();

        await using (var ctx = NewContext())
        {
            var repo = new CookEventRepository(ctx);
            var cookEvent = CookEvent.Record(_recipeId, _household, 2, userId, _clock).Value;
            var line = cookEvent.AddConsumeLine(Guid.CreateVersion7(), productId, 150m, unitId);
            line.MarkDeferredUnitGap(); // the widened ck_cook_consume_line_status CHECK must admit this
            await repo.AddAsync(cookEvent);
            await repo.SaveChangesAsync();
        }

        await using var ctx2 = NewContext();
        var events = await new CookEventRepository(ctx2)
            .ListWithDeferredUnitGapLinesForProductsAsync([productId]);

        var loaded = Assert.Single(events);
        var loadedLine = Assert.Single(loaded.ConsumeLines);
        Assert.Equal(CookConsumeLineStatus.DeferredUnitGap, loadedLine.Status);
        Assert.Equal(productId, loadedLine.ProductId);
        Assert.Equal(150m, loadedLine.Shortfall);
    }

    [Fact(DisplayName = "SupersededByCount line persists (CHECK admits it)")]
    public async Task SupersededByCount_Line_Persists()
    {
        var userId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();

        await using (var ctx = NewContext())
        {
            var repo = new CookEventRepository(ctx);
            var cookEvent = CookEvent.Record(_recipeId, _household, 2, userId, _clock).Value;
            var line = cookEvent.AddConsumeLine(Guid.CreateVersion7(), productId, 50m, Guid.CreateVersion7());
            line.MarkDeferredUnitGap();
            line.MarkSupersededByCount();
            await repo.AddAsync(cookEvent);
            await repo.SaveChangesAsync(); // must not violate the status CHECK
        }

        // A voided line is not returned by the deferred-line query.
        await using var ctx2 = NewContext();
        var events = await new CookEventRepository(ctx2)
            .ListWithDeferredUnitGapLinesForProductsAsync([productId]);
        Assert.Empty(events);
    }

    [Fact(DisplayName = "ListWithDeferredUnitGapLinesForProductsAsync filters by product and status")]
    public async Task ListWithDeferredUnitGapLines_Filters_By_Product_And_Status()
    {
        var userId = Guid.CreateVersion7();
        var deferredProduct = Guid.CreateVersion7();
        var otherProduct = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();

        await using (var ctx = NewContext())
        {
            var repo = new CookEventRepository(ctx);
            var cookEvent = CookEvent.Record(_recipeId, _household, 2, userId, _clock).Value;
            // Deferred line for the product we query.
            cookEvent.AddConsumeLine(Guid.CreateVersion7(), deferredProduct, 20m, unitId).MarkDeferredUnitGap();
            // An Applied line on a different product must not match.
            cookEvent.AddConsumeLine(Guid.CreateVersion7(), otherProduct, 30m, unitId).MarkApplied(0m);
            await repo.AddAsync(cookEvent);
            await repo.SaveChangesAsync();
        }

        await using var ctx2 = NewContext();
        var repo2 = new CookEventRepository(ctx2);

        // Querying the OTHER (applied) product returns nothing.
        Assert.Empty(await repo2.ListWithDeferredUnitGapLinesForProductsAsync([otherProduct]));
        // Empty product set returns nothing.
        Assert.Empty(await repo2.ListWithDeferredUnitGapLinesForProductsAsync([]));
        // Querying the deferred product returns the event.
        Assert.Single(await repo2.ListWithDeferredUnitGapLinesForProductsAsync([deferredProduct]));
    }

    // ── GetLatestCookedAtByPlannedDishIdsAsync (plantry-0eut cook-status read port) ─────────────

    [Fact(DisplayName = "GetLatestCookedAtByPlannedDishIdsAsync resolves a cooked plan dish's CookedAt")]
    public async Task GetLatestCookedAtByPlannedDishIdsAsync_Resolves_A_Cooked_Dish()
    {
        var userId = Guid.CreateVersion7();
        var plannedDishId = Guid.NewGuid();
        var cookedAt = new DateTimeOffset(2026, 7, 24, 18, 42, 0, TimeSpan.Zero);

        await using (var ctx = NewContext())
        {
            var repo = new CookEventRepository(ctx);
            var cookEvent = CookEvent.Record(_recipeId, _household, 2, userId, new FixedClock(cookedAt), plannedDishId).Value;
            await repo.AddAsync(cookEvent);
            await repo.SaveChangesAsync();
        }

        await using var ctx2 = NewContext();
        var result = await new CookEventRepository(ctx2).GetLatestCookedAtByPlannedDishIdsAsync([plannedDishId]);

        Assert.Equal(cookedAt, Assert.Single(result).Value);
    }

    [Fact(DisplayName = "GetLatestCookedAtByPlannedDishIdsAsync omits a plan dish never cooked (direct cooks have a null PlannedDishId)")]
    public async Task GetLatestCookedAtByPlannedDishIdsAsync_Omits_An_Unmatched_Dish()
    {
        var userId = Guid.CreateVersion7();

        await using (var ctx = NewContext())
        {
            var repo = new CookEventRepository(ctx);
            // Direct recipe-launched cook — PlannedDishId defaults to null, never matches any dish id.
            var cookEvent = CookEvent.Record(_recipeId, _household, 2, userId, _clock).Value;
            await repo.AddAsync(cookEvent);
            await repo.SaveChangesAsync();
        }

        await using var ctx2 = NewContext();
        var result = await new CookEventRepository(ctx2).GetLatestCookedAtByPlannedDishIdsAsync([Guid.NewGuid()]);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "GetLatestCookedAtByPlannedDishIdsAsync resolves the most recent cook when a dish was cooked twice")]
    public async Task GetLatestCookedAtByPlannedDishIdsAsync_Resolves_The_Most_Recent_Cook()
    {
        var userId = Guid.CreateVersion7();
        var plannedDishId = Guid.NewGuid();
        var earlier = new DateTimeOffset(2026, 7, 24, 18, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 7, 24, 19, 30, 0, TimeSpan.Zero);

        await using (var ctx = NewContext())
        {
            var repo = new CookEventRepository(ctx);
            await repo.AddAsync(CookEvent.Record(_recipeId, _household, 2, userId, new FixedClock(earlier), plannedDishId).Value);
            await repo.AddAsync(CookEvent.Record(_recipeId, _household, 2, userId, new FixedClock(later), plannedDishId).Value);
            await repo.SaveChangesAsync();
        }

        await using var ctx2 = NewContext();
        var result = await new CookEventRepository(ctx2).GetLatestCookedAtByPlannedDishIdsAsync([plannedDishId]);

        Assert.Equal(later, Assert.Single(result).Value);
    }

    [Fact(DisplayName = "GetLatestCookedAtByPlannedDishIdsAsync returns empty for empty input")]
    public async Task GetLatestCookedAtByPlannedDishIdsAsync_Returns_Empty_For_Empty_Input()
    {
        await using var ctx = NewContext();
        var result = await new CookEventRepository(ctx).GetLatestCookedAtByPlannedDishIdsAsync([]);

        Assert.Empty(result);
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
