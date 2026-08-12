using Microsoft.EntityFrameworkCore;
using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Plantry.Planning.Infrastructure;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.MealPlanning;
using Xunit;

namespace Plantry.Tests.Integration.MealPlanning;

/// <summary>
/// L3 coverage for the Composition join that turns retained plans and cook events into Planning's
/// single shared history snapshot. Uses both real bounded-context schemas and their household filters.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class RecentMealHistoryReaderAdapterTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly DateOnly Today = new(2026, 8, 9);
    private static readonly DateOnly CurrentWeek = MealPlan.NormalizeToMonday(Today);
    private static readonly IClock Clock = new FixedClock(
        new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero),
        TimeZoneInfo.Utc);

    private HouseholdId _household;
    private MealSlotId _slotId;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
        _slotId = MealSlotId.New();
        await SeedSlotAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ReadAsync_NoHistory_ReturnsEmptySnapshot()
    {
        var snapshot = await ReadAsync();

        Assert.Empty(snapshot.Recipes);
    }

    [Fact]
    public async Task ReadAsync_PlanOnly_UsesRetainedMealDateAndPolicyWeight()
    {
        var recipe = await SeedRecipeAsync("Plan-only soup");
        await SeedPlannedDishAsync(recipe.Id.Value, Today.AddDays(-7));

        var snapshot = await ReadAsync();

        var history = Assert.Single(snapshot.Recipes);
        Assert.Equal(recipe.Id.Value, history.RecipeId);
        var occurrence = Assert.Single(history.Occurrences);
        Assert.Equal(RecentMealOccurrenceSource.RetainedPlan, occurrence.Source);
        Assert.Equal(Today.AddDays(-7), occurrence.OccurredOn);
        Assert.Equal(0.20m, occurrence.NoveltyWeight);
        Assert.Null(occurrence.CookedAt);
    }

    [Fact]
    public async Task ReadAsync_CookOnly_UsesActualCookTimestamp()
    {
        var recipe = await SeedRecipeAsync("Cook-only curry");
        var cookedAt = new DateTimeOffset(2026, 7, 26, 18, 30, 0, TimeSpan.Zero);
        await SeedCookAsync(recipe.Id, cookedAt);

        var snapshot = await ReadAsync();

        var occurrence = Assert.Single(Assert.Single(snapshot.Recipes).Occurrences);
        Assert.Equal(RecentMealOccurrenceSource.CookEvent, occurrence.Source);
        Assert.Equal(Today.AddDays(-14), occurrence.OccurredOn);
        Assert.Equal(cookedAt, occurrence.CookedAt);
        Assert.Equal(0.10m, occurrence.NoveltyWeight);
    }

    [Fact]
    public async Task ReadAsync_DistinctCookEvents_SumTheirDecayedContributions()
    {
        var recipe = await SeedRecipeAsync("Weekly chili");
        await SeedCookAsync(recipe.Id, new DateTimeOffset(2026, 8, 2, 18, 0, 0, TimeSpan.Zero));
        await SeedCookAsync(recipe.Id, new DateTimeOffset(2026, 7, 26, 18, 0, 0, TimeSpan.Zero));

        var snapshot = await ReadAsync();

        var history = Assert.Single(snapshot.Recipes);
        Assert.Equal(2, history.Occurrences.Count);
        Assert.Equal(0.30m, history.RecencyScore);
    }

    [Fact]
    public async Task ReadAsync_LinkedPlanAndCook_CountsOnceAndPrefersCookedAt()
    {
        var recipe = await SeedRecipeAsync("Linked noodles");
        var plannedDishId = await SeedPlannedDishAsync(recipe.Id.Value, Today.AddDays(-14));
        var cookedAt = new DateTimeOffset(2026, 8, 2, 20, 0, 0, TimeSpan.Zero);
        await SeedCookAsync(recipe.Id, cookedAt, plannedDishId);

        var snapshot = await ReadAsync();

        var history = Assert.Single(snapshot.Recipes);
        var occurrence = Assert.Single(history.Occurrences);
        Assert.Equal(RecentMealOccurrenceSource.CookEvent, occurrence.Source);
        Assert.Equal(Today.AddDays(-7), occurrence.OccurredOn);
        Assert.Equal(cookedAt, occurrence.CookedAt);
        Assert.Equal(0.20m, history.RecencyScore);
    }

    [Fact]
    public async Task ReadAsync_LinkedCookOutsideHorizon_SuppressesInHorizonPlannedDate()
    {
        var recipe = await SeedRecipeAsync("Late-linked stew");
        var plannedDishId = await SeedPlannedDishAsync(recipe.Id.Value, Today.AddDays(-7));
        await SeedCookAsync(
            recipe.Id,
            new DateTimeOffset(2026, 7, 18, 18, 0, 0, TimeSpan.Zero),
            plannedDishId);

        var snapshot = await ReadAsync();

        Assert.Empty(snapshot.Recipes);
    }

    [Fact]
    public async Task ReadAsync_ArchivedRecipeRetainsIdentityRecencyAndSemanticFacets()
    {
        var recipe = await SeedRecipeAsync(
            "Archived tofu bowl",
            archived: true,
            facet: ("Tofu", TagCategory.Protein));
        await SeedCookAsync(
            recipe.Id,
            new DateTimeOffset(2026, 8, 2, 18, 0, 0, TimeSpan.Zero));

        var snapshot = await ReadAsync();

        var history = Assert.Single(snapshot.Recipes);
        Assert.True(history.IsArchived);
        Assert.Equal("Archived tofu bowl", history.Name);
        var facet = Assert.Single(history.Facets);
        Assert.Equal("Tofu", facet.Name);
        Assert.Equal("Protein", facet.Category);
        Assert.Equal(0.20m, history.RecencyScore);
    }

    [Fact]
    public async Task ReadAsync_ExcludedWeekStaysSeparateFromRetainedHistory()
    {
        var recipe = await SeedRecipeAsync("Current-week pasta");
        var plannedDishId = await SeedPlannedDishAsync(recipe.Id.Value, Today.AddDays(-1));
        await SeedCookAsync(
            recipe.Id,
            new DateTimeOffset(2026, 8, 8, 18, 0, 0, TimeSpan.Zero),
            plannedDishId);

        var snapshot = await ReadAsync();

        Assert.Empty(snapshot.Recipes);
    }

    private async Task<RecentMealHistorySnapshot> ReadAsync()
    {
        await using var planning = NewPlanningDb();
        await using var recipes = NewRecipesDb();
        var adapter = new RecentMealHistoryReaderAdapter(planning, recipes, Clock);
        return await adapter.ReadAsync(_household, Today, CurrentWeek);
    }

    private async Task<Recipe> SeedRecipeAsync(
        string name,
        bool archived = false,
        (string Name, TagCategory Category)? facet = null)
    {
        await using var recipes = NewRecipesDb();
        var recipe = Recipe.Create(_household, name, 2, Clock).Value;

        if (facet is { } facetValue)
        {
            var tag = Tag.Create(_household, facetValue.Name, facetValue.Category, Clock);
            recipes.Tags.Add(tag);
            recipe.SetTags([tag.Id], Clock);
        }

        if (archived) recipe.Archive(Clock);
        recipes.Recipes.Add(recipe);
        await recipes.SaveChangesAsync();
        return recipe;
    }

    private async Task<Guid> SeedPlannedDishAsync(Guid recipeId, DateOnly date)
    {
        await using var planning = NewPlanningDb();
        var plan = MealPlan.Start(_household, date, Clock);
        plan.AssignMeal(
            date,
            _slotId,
            [DishSpec.ForRecipe(recipeId, 2)],
            attendeesOverride: null,
            source: "manual",
            createdBy: Guid.NewGuid(),
            clock: Clock);
        planning.MealPlans.Add(plan);
        await planning.SaveChangesAsync();
        return plan.PlannedMeals.Single().PlannedDishes.Single().Id.Value;
    }

    private async Task SeedCookAsync(RecipeId recipeId, DateTimeOffset cookedAt, Guid? plannedDishId = null)
    {
        await using var recipes = NewRecipesDb();
        var cook = CookEvent.Record(
            recipeId,
            _household,
            servingsCooked: 2,
            cookedBy: Guid.NewGuid(),
            clock: new FixedClock(cookedAt, TimeZoneInfo.Utc),
            plannedDishId: plannedDishId).Value;
        recipes.CookEvents.Add(cook);
        await recipes.SaveChangesAsync();
    }

    private async Task SeedSlotAsync()
    {
        await using var planning = NewPlanningDb();
        var configId = Guid.NewGuid();
        await planning.Database.ExecuteSqlRawAsync(@"
            INSERT INTO meal_planning.meal_slot_config
                (meal_slot_config_id, household_id, created_at, updated_at)
            VALUES ({0}, {1}, NOW(), NOW());
            INSERT INTO meal_planning.meal_slot
                (meal_slot_id, household_id, meal_slot_config_id, label, ordinal, default_attendees)
            VALUES ({2}, {1}, {0}, 'Dinner', 1, '{{}}');",
            configId,
            _household.Value,
            _slotId.Value);
    }

    private PlanningDbContext NewPlanningDb()
    {
        var options = new DbContextOptionsBuilder<PlanningDbContext>()
            .UseNpgsql(db.ConnectionString)
            .Options;
        var context = new PlanningDbContext(options);
        context.SetHouseholdId(_household.Value);
        return context;
    }

    private RecipesDbContext NewRecipesDb()
    {
        var options = new DbContextOptionsBuilder<RecipesDbContext>()
            .UseNpgsql(db.ConnectionString)
            .Options;
        var context = new RecipesDbContext(options);
        context.SetHouseholdId(_household.Value);
        return context;
    }
}
