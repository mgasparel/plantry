using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Xunit;

namespace Plantry.Tests.Unit.MealPlanning.Domain;

/// <summary>
/// L1 unit tests for <see cref="PlanFulfillmentService"/>.
/// Uses in-memory fakes — no EF, no DB, no real adapters.
/// </summary>
public sealed class PlanFulfillmentServiceTests
{
    private static readonly HouseholdId HouseholdId = HouseholdId.New();
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly DateOnly Monday = new(2026, 6, 9); // known Monday
    private static readonly MealSlotId SlotA = MealSlotId.New();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 6, 11);

    // ── RollUpMealAsync — note meal ───────────────────────────────────────────

    [Fact]
    public async Task RollUpMealAsync_ReturnsNone_ForNoteMeal()
    {
        var svc = BuildService();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignNote(Monday, SlotA, "Takeout", null, "manual", UserId, Clock);
        var meal = plan.PlannedMeals[0];

        var result = await svc.RollUpMealAsync(meal, Today);

        Assert.Equal(MealFulfillment.None.FulfillmentPercent, result.FulfillmentPercent);
        Assert.False(result.HasExpiringIngredients);
    }

    // ── RollUpMealAsync — recipe dish 100% ────────────────────────────────────

    [Fact]
    public async Task RollUpMealAsync_Returns100_WhenRecipeFullyStocked()
    {
        var recipeId = Guid.NewGuid();
        var enrichment = new RecipeDishEnrichment(100, 12.50m, false, false);
        var fakeReader = new FakeEnrichmentRecipeReader(recipeId, enrichment);

        var svc = BuildService(recipeReader: fakeReader);
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Recipe, recipeId, 2)], null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0], Today);

        Assert.Equal(100, result.FulfillmentPercent);
        Assert.False(result.HasExpiringIngredients);
    }

    // ── RollUpMealAsync — recipe dish 0% (not found) ─────────────────────────

    [Fact]
    public async Task RollUpMealAsync_Returns0_WhenRecipeEnrichmentNull()
    {
        var fakeReader = new FakeEnrichmentRecipeReader(Guid.NewGuid(), null);
        var svc = BuildService(recipeReader: fakeReader);
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Recipe, Guid.NewGuid(), 2)], null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0], Today);

        Assert.Equal(0, result.FulfillmentPercent);
    }

    // ── RollUpMealAsync — recipe dish with expiring ingredients ──────────────

    [Fact]
    public async Task RollUpMealAsync_SetsHasExpiring_WhenEnrichmentSaysExpiring()
    {
        var recipeId = Guid.NewGuid();
        var enrichment = new RecipeDishEnrichment(75, null, false, true);
        var svc = BuildService(recipeReader: new FakeEnrichmentRecipeReader(recipeId, enrichment));
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Recipe, recipeId, 2)], null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0], Today);

        Assert.True(result.HasExpiringIngredients);
    }

    // ── RollUpMealAsync — product dish in stock ───────────────────────────────

    [Fact]
    public async Task RollUpMealAsync_Returns100_WhenProductDishFullyStocked()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        var fakeStock = new FakeStockReader(
            new MealPlanProductStock(productId, 10m, unitId, null)); // 10 units, 2 servings needed

        var svc = BuildService(stockReader: fakeStock);
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Product, productId, 2)], null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0], Today);

        Assert.Equal(100, result.FulfillmentPercent);
    }

    // ── RollUpMealAsync — product dish out of stock ───────────────────────────

    [Fact]
    public async Task RollUpMealAsync_Returns0_WhenProductDishNotStocked()
    {
        var productId = Guid.NewGuid();
        var fakeStock = new FakeStockReader(null); // no stock

        var svc = BuildService(stockReader: fakeStock);
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Product, productId, 2)], null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0], Today);

        Assert.Equal(0, result.FulfillmentPercent);
    }

    // ── RollUpMealAsync — product dish — Use-soon badge ──────────────────────

    [Fact]
    public async Task RollUpMealAsync_SetsHasExpiring_WhenProductExpiresSoon()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        var expiry = Today.AddDays(PlanFulfillmentService.ExpiringSoonDays - 1); // within window
        var fakeStock = new FakeStockReader(
            new MealPlanProductStock(productId, 10m, unitId, expiry));

        var svc = BuildService(stockReader: fakeStock);
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Product, productId, 2)], null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0], Today);

        Assert.True(result.HasExpiringIngredients);
    }

    // ── RollUpMealAsync — multi-dish average ─────────────────────────────────

    [Fact]
    public async Task RollUpMealAsync_AveragesAcrossMultipleDishes()
    {
        // Two recipe dishes: 100% and 0%  →  average = 50%
        var recipeA = Guid.NewGuid();
        var recipeB = Guid.NewGuid();
        var reader = new FakeMultiEnrichmentReader([
            (recipeA, new RecipeDishEnrichment(100, null, false, false)),
            (recipeB, null), // 0%
        ]);

        var svc = BuildService(recipeReader: reader);
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [
            new DishSpec(DishKind.Recipe, recipeA, 2),
            new DishSpec(DishKind.Recipe, recipeB, 2),
        ], null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0], Today);

        Assert.Equal(50, result.FulfillmentPercent);
    }

    // ── RollUpWeekAsync — note meals skipped ─────────────────────────────────

    [Fact]
    public async Task RollUpWeekAsync_SkipsNoteMeals()
    {
        var svc = BuildService();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignNote(Monday, SlotA, "Takeout", null, "manual", UserId, Clock);

        var result = await svc.RollUpWeekAsync(plan, Today);

        Assert.Equal(MealFulfillment.None.FulfillmentPercent, result.FulfillmentPercent);
    }

    // ── RollUpWeekAsync — week roll-up ────────────────────────────────────────

    [Fact]
    public async Task RollUpWeekAsync_CombinesAcrossMultipleMeals()
    {
        var recipeId = Guid.NewGuid();
        var enrichment = new RecipeDishEnrichment(60, null, false, false);
        var reader = new FakeEnrichmentRecipeReader(recipeId, enrichment);
        var svc = BuildService(recipeReader: reader);

        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA,
            [new DishSpec(DishKind.Recipe, recipeId, 2)], null, "manual", UserId, Clock);
        plan.AssignMeal(Monday.AddDays(1), SlotA,
            [new DishSpec(DishKind.Recipe, recipeId, 4)], null, "manual", UserId, Clock);

        var result = await svc.RollUpWeekAsync(plan, Today);

        Assert.Equal(60, result.FulfillmentPercent); // both 60% → average 60%
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static PlanFulfillmentService BuildService(
        IRecipeReadModel? recipeReader = null,
        IMealPlanStockReader? stockReader = null)
        => new(
            recipeReader ?? new FakeEnrichmentRecipeReader(Guid.Empty, null),
            stockReader ?? new FakeStockReader(null));
}

// ── test doubles ──────────────────────────────────────────────────────────────

/// <summary>Fake <see cref="IRecipeReadModel"/> that returns a fixed enrichment for one recipe ID.</summary>
internal sealed class FakeEnrichmentRecipeReader(Guid recipeId, RecipeDishEnrichment? enrichment) : IRecipeReadModel
{
    public Task<RecipeReadModel?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<RecipeReadModel?>(null);

    public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string q, int max, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeReadModel>>([]);

    public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid id, int servings, DateOnly today, CancellationToken ct = default)
        => Task.FromResult(id == recipeId ? enrichment : null);

    public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid id, int servings, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeMissingIngredient>>([]);

    public Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default)
        => Task.FromResult(true);
}

/// <summary>Fake <see cref="IRecipeReadModel"/> that supports multiple recipe IDs.</summary>
internal sealed class FakeMultiEnrichmentReader(
    IReadOnlyList<(Guid RecipeId, RecipeDishEnrichment? Enrichment)> map) : IRecipeReadModel
{
    public Task<RecipeReadModel?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<RecipeReadModel?>(null);

    public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string q, int max, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeReadModel>>([]);

    public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid id, int servings, DateOnly today, CancellationToken ct = default)
    {
        var match = map.FirstOrDefault(m => m.RecipeId == id);
        return Task.FromResult(match.Enrichment);
    }

    public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid id, int servings, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeMissingIngredient>>([]);

    public Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default)
        => Task.FromResult(true);
}

/// <summary>Fake <see cref="IMealPlanStockReader"/> that returns a fixed stock record.</summary>
internal sealed class FakeStockReader(MealPlanProductStock? stock) : IMealPlanStockReader
{
    public Task<MealPlanProductStock?> FindStockAsync(Guid productId, CancellationToken ct = default)
        => Task.FromResult(stock);
}
