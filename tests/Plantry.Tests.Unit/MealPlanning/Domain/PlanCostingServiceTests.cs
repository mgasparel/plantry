using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Xunit;

namespace Plantry.Tests.Unit.MealPlanning.Domain;

/// <summary>
/// L1 unit tests for <see cref="PlanCostingService"/>.
/// Uses in-memory fakes — no EF, no DB, no real adapters.
/// </summary>
public sealed class PlanCostingServiceTests
{
    private static readonly HouseholdId HouseholdId = HouseholdId.New();
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly DateOnly Monday = new(2026, 6, 9);
    private static readonly MealSlotId SlotA = MealSlotId.New();
    private static readonly Guid UserId = Guid.NewGuid();

    // ── Note meal → None ──────────────────────────────────────────────────────

    [Fact]
    public async Task RollUpMealAsync_ReturnsNone_ForNoteMeal()
    {
        var svc = BuildService();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignNote(Monday, SlotA, "Takeout", null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0]);

        Assert.Equal(MealCost.None, result);
    }

    // ── Recipe dish — full cost ────────────────────────────────────────────────

    [Fact]
    public async Task RollUpMealAsync_ReturnsFullCost_WhenRecipeHasCompleteEnrichment()
    {
        var recipeId = Guid.NewGuid();
        var enrichment = new RecipeDishEnrichment(100, 6.00m, false, false); // $6 total for 2 servings
        var reader = new FakePriceEnrichmentReader(recipeId, enrichment);

        var svc = BuildService(recipeReader: reader);
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Recipe, recipeId, 2)], null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0]);

        Assert.Equal(CostCompleteness.Full, result.Completeness);
        Assert.Equal(6.00m, result.Amount);
    }

    // ── Recipe dish — partial cost ─────────────────────────────────────────────

    [Fact]
    public async Task RollUpMealAsync_ReturnsPartial_WhenEnrichmentIsPartial()
    {
        var recipeId = Guid.NewGuid();
        var enrichment = new RecipeDishEnrichment(80, 4.00m, true, false);
        var reader = new FakePriceEnrichmentReader(recipeId, enrichment);

        var svc = BuildService(recipeReader: reader);
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Recipe, recipeId, 2)], null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0]);

        Assert.Equal(CostCompleteness.Partial, result.Completeness);
    }

    // ── Recipe dish — no cost data ────────────────────────────────────────────

    [Fact]
    public async Task RollUpMealAsync_ReturnsNone_WhenNoEnrichment()
    {
        var reader = new FakePriceEnrichmentReader(Guid.NewGuid(), null);
        var svc = BuildService(recipeReader: reader);
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Recipe, Guid.NewGuid(), 2)], null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0]);

        Assert.Equal(MealCost.None, result);
    }

    // ── Product dish — priced ─────────────────────────────────────────────────

    [Fact]
    public async Task RollUpMealAsync_ComputesCostForProductDish()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        // UnitPrice = $2/unit, servings = 3 → expected cost = $6
        var pricePoint = new MealPlanPricePoint(productId, 10m, 1m, unitId, 2m);
        var fakePriceReader = new FakePriceReader(pricePoint);

        var svc = BuildService(priceReader: fakePriceReader);
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Product, productId, 3)], null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0]);

        Assert.Equal(CostCompleteness.Full, result.Completeness);
        Assert.Equal(6m, result.Amount);
    }

    // ── Product dish — derived price when UnitPrice is null ──────────────────

    [Fact]
    public async Task RollUpMealAsync_DerivesPriceFromPriceAndQuantity_WhenUnitPriceAbsent()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        // Price = $5 for qty 2 → unit price = $2.50; servings = 2 → expected $5
        var pricePoint = new MealPlanPricePoint(productId, 5m, 2m, unitId, null);
        var fakePriceReader = new FakePriceReader(pricePoint);

        var svc = BuildService(priceReader: fakePriceReader);
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Product, productId, 2)], null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0]);

        Assert.Equal(5m, result.Amount);
    }

    // ── Product dish — no price data ──────────────────────────────────────────

    [Fact]
    public async Task RollUpMealAsync_ReturnsNone_WhenProductHasNoPriceData()
    {
        var svc = BuildService(priceReader: new FakePriceReader(null));
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Product, Guid.NewGuid(), 2)], null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0]);

        Assert.Equal(MealCost.None, result);
    }

    // ── Multi-dish — partial completeness when one dish unpriced ─────────────

    [Fact]
    public async Task RollUpMealAsync_ReturnsPartial_WhenOneDishIsUnpriced()
    {
        var pricedRecipe = Guid.NewGuid();
        var unpricedRecipe = Guid.NewGuid();
        var reader = new FakeMultiPriceEnrichmentReader([
            (pricedRecipe,   new RecipeDishEnrichment(100, 4m, false, false)),
            (unpricedRecipe, new RecipeDishEnrichment(100, null, false, false)),
        ]);

        var svc = BuildService(recipeReader: reader);
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [
            new DishSpec(DishKind.Recipe, pricedRecipe, 2),
            new DishSpec(DishKind.Recipe, unpricedRecipe, 2),
        ], null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0]);

        Assert.Equal(CostCompleteness.Partial, result.Completeness);
        Assert.Equal(4m, result.Amount); // only the priced one contributes
    }

    // ── Week roll-up — sums across meals ──────────────────────────────────────

    [Fact]
    public async Task RollUpWeekAsync_SumsAcrossMultipleMeals()
    {
        var recipeId = Guid.NewGuid();
        var enrichment = new RecipeDishEnrichment(100, 5m, false, false);
        var reader = new FakePriceEnrichmentReader(recipeId, enrichment);
        var svc = BuildService(recipeReader: reader);

        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA,
            [new DishSpec(DishKind.Recipe, recipeId, 2)], null, "manual", UserId, Clock);
        plan.AssignMeal(Monday.AddDays(1), SlotA,
            [new DishSpec(DishKind.Recipe, recipeId, 2)], null, "manual", UserId, Clock);

        var result = await svc.RollUpWeekAsync(plan);

        Assert.Equal(10m, result.Amount); // 5 + 5
        Assert.Equal(CostCompleteness.Full, result.Completeness);
    }

    // ── Week roll-up — skips note meals ───────────────────────────────────────

    [Fact]
    public async Task RollUpWeekAsync_SkipsNoteMeals()
    {
        var svc = BuildService();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignNote(Monday, SlotA, "Takeout", null, "manual", UserId, Clock);

        var result = await svc.RollUpWeekAsync(plan);

        Assert.Equal(MealCost.None, result);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static PlanCostingService BuildService(
        IRecipeReadModel? recipeReader = null,
        IMealPlanPriceReader? priceReader = null)
        => new(
            recipeReader ?? new FakePriceEnrichmentReader(Guid.Empty, null),
            priceReader ?? new FakePriceReader(null));
}

// ── test doubles ──────────────────────────────────────────────────────────────

internal sealed class FakePriceEnrichmentReader(Guid recipeId, RecipeDishEnrichment? enrichment) : IRecipeReadModel
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

internal sealed class FakeMultiPriceEnrichmentReader(
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

internal sealed class FakePriceReader(MealPlanPricePoint? price) : IMealPlanPriceReader
{
    public Task<MealPlanPricePoint?> FindLatestAsync(Guid productId, CancellationToken ct = default)
        => Task.FromResult(price);
}
