using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Xunit;

namespace Plantry.Tests.Unit.MealPlanning.Domain;

/// <summary>
/// L1 unit tests for <see cref="PlanInsightsService"/> (P3-5).
/// Each test exercises one insight rule in isolation via faked ports.
/// L2 composition: <see cref="InspectAsync_Composition_AllRulesRunViaFakeAdapters"/> verifies the
/// service composes correctly over both faked ports.
/// </summary>
public sealed class PlanInsightsServiceTests
{
    private static readonly HouseholdId HouseholdId = HouseholdId.New();
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly DateOnly Monday = new(2026, 6, 8); // known Monday (June 8 2026 is a Monday)
    private static readonly MealSlotId SlotA = MealSlotId.New();
    private static readonly MealSlotId SlotB = MealSlotId.New();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 6, 11);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PlanInsightsService BuildService(
        IMealPlanExpiringStockReader? expiringReader = null,
        IRecipeReadModel? recipeReader = null)
        => new(
            expiringReader ?? new FakeExpiringStockReader([]),
            recipeReader ?? new FakeNoOpRecipeReader());

    /// <summary>
    /// Builds the all-cell list for a 7-day plan with the given slots.
    /// </summary>
    private static List<string> AllCells(IEnumerable<MealSlotId> slotIds)
    {
        var cells = new List<string>();
        for (int i = 0; i < 7; i++)
        {
            var date = Monday.AddDays(i);
            foreach (var slotId in slotIds)
                cells.Add($"{date:yyyy-MM-dd}_{slotId.Value:N}");
        }
        return cells;
    }

    // ── Rule 1: UnusedExpiring ────────────────────────────────────────────────

    [Fact(DisplayName = "Rule 1 — expiring product not used by any planned meal → UnusedExpiring callout")]
    public async Task Rule1_UnusedExpiring_AppearsWhenExpiringProductNotInPlan()
    {
        var expiringProductId = Guid.NewGuid();
        var expiringReader = new FakeExpiringStockReader([expiringProductId]);
        // Recipe reader returns enrichment without HasExpiringIngredients = false for all recipes
        var recipeReader = new FakeNoOpRecipeReader();

        var svc = BuildService(expiringReader, recipeReader);
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignNote(Monday, SlotA, "Takeout", null, "manual", UserId, Clock);

        var result = await svc.InspectAsync(plan, AllCells([SlotA]), null, null, null, Today);

        var insight = Assert.Single(result.Insights, i => i.Kind == InsightKind.UnusedExpiring);
        Assert.Equal("warn", insight.Tone);
        Assert.Equal("clock", insight.Icon);
        Assert.Equal("/Recipes?filter=use-soon", insight.ActionUrl);
    }

    [Fact(DisplayName = "Rule 1 — expiring product used by a planned recipe → no UnusedExpiring callout")]
    public async Task Rule1_UnusedExpiring_SuppressedWhenRecipeUsesExpiringIngredient()
    {
        var expiringProductId = Guid.NewGuid();
        var expiringReader = new FakeExpiringStockReader([expiringProductId]);
        var recipeId = Guid.NewGuid();
        // The recipe has expiring ingredients — meaning it will consume the expiring stock
        var recipeReader = new FakeEnrichmentRecipeReader(recipeId,
            new RecipeDishEnrichment(100, null, false, HasExpiringIngredients: true));

        var svc = BuildService(expiringReader, recipeReader);
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Recipe, recipeId, 2)], null, "manual", UserId, Clock);

        var result = await svc.InspectAsync(plan, AllCells([SlotA]), null, null, null, Today);

        Assert.DoesNotContain(result.Insights, i => i.Kind == InsightKind.UnusedExpiring);
    }

    [Fact(DisplayName = "Rule 1 — no expiring stock → no UnusedExpiring callout")]
    public async Task Rule1_UnusedExpiring_NoCalloutWhenNoExpiringStock()
    {
        var svc = BuildService(new FakeExpiringStockReader([]));
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);

        var result = await svc.InspectAsync(plan, AllCells([SlotA]), null, null, null, Today);

        Assert.DoesNotContain(result.Insights, i => i.Kind == InsightKind.UnusedExpiring);
    }

    // ── Rule 2: OverBudget ────────────────────────────────────────────────────

    [Fact(DisplayName = "Rule 2 — week cost exceeds budget target → OverBudget callout")]
    public async Task Rule2_OverBudget_AppearsWhenCostExceedsBudget()
    {
        var svc = BuildService();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);

        var result = await svc.InspectAsync(plan, [], weekTotalCost: 120m, budgetTarget: 80m, null, Today);

        var insight = Assert.Single(result.Insights, i => i.Kind == InsightKind.OverBudget);
        Assert.Equal("warn", insight.Tone);
        Assert.Equal("dollar-sign", insight.Icon);
        Assert.Contains("$120", insight.Title);
        Assert.Contains("$80", insight.Title);
    }

    [Fact(DisplayName = "Rule 2 — week cost under budget → no OverBudget callout")]
    public async Task Rule2_OverBudget_NoCalloutWhenUnderBudget()
    {
        var svc = BuildService();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);

        var result = await svc.InspectAsync(plan, [], weekTotalCost: 60m, budgetTarget: 80m, null, Today);

        Assert.DoesNotContain(result.Insights, i => i.Kind == InsightKind.OverBudget);
    }

    [Fact(DisplayName = "Rule 2 — no budget target → over-budget rule suppressed")]
    public async Task Rule2_OverBudget_SuppressedWhenBudgetTargetIsNull()
    {
        var svc = BuildService();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);

        var result = await svc.InspectAsync(plan, [], weekTotalCost: 200m, budgetTarget: null, null, Today);

        Assert.DoesNotContain(result.Insights, i => i.Kind == InsightKind.OverBudget);
    }

    // ── Rule 3: RepetitionThisWeek ────────────────────────────────────────────

    [Fact(DisplayName = "Rule 3 — same recipe appears twice in one week → RepetitionThisWeek callout")]
    public async Task Rule3_RepetitionThisWeek_AppearsWhenRecipeRepeats()
    {
        var recipeId = Guid.NewGuid();
        var svc = BuildService();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Recipe, recipeId, 2)], null, "manual", UserId, Clock);
        plan.AssignMeal(Monday.AddDays(1), SlotA, [new DishSpec(DishKind.Recipe, recipeId, 2)], null, "manual", UserId, Clock);

        var result = await svc.InspectAsync(plan, AllCells([SlotA, SlotB]), null, null, null, Today);

        Assert.Contains(result.Insights, i => i.Kind == InsightKind.RepetitionThisWeek);
    }

    [Fact(DisplayName = "Rule 3 — all recipes appear only once → no RepetitionThisWeek callout")]
    public async Task Rule3_RepetitionThisWeek_NoCalloutWhenAllUnique()
    {
        var svc = BuildService();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Recipe, Guid.NewGuid(), 2)], null, "manual", UserId, Clock);
        plan.AssignMeal(Monday.AddDays(1), SlotA, [new DishSpec(DishKind.Recipe, Guid.NewGuid(), 2)], null, "manual", UserId, Clock);

        var result = await svc.InspectAsync(plan, AllCells([SlotA]), null, null, null, Today);

        Assert.DoesNotContain(result.Insights, i => i.Kind == InsightKind.RepetitionThisWeek);
    }

    // ── Rule 4: RepetitionVsHistory ───────────────────────────────────────────

    [Fact(DisplayName = "Rule 4 — recipe in current plan also in prior plan → RepetitionVsHistory callout")]
    public async Task Rule4_RepetitionVsHistory_AppearsWhenRecipeRepeatsVsHistory()
    {
        var sharedRecipeId = Guid.NewGuid();
        var svc = BuildService();

        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Recipe, sharedRecipeId, 2)], null, "manual", UserId, Clock);

        // Prior plan from last week
        var lastMonday = Monday.AddDays(-7);
        var priorPlan = MealPlan.Start(HouseholdId, lastMonday, Clock);
        priorPlan.AssignMeal(lastMonday, SlotA, [new DishSpec(DishKind.Recipe, sharedRecipeId, 2)], null, "manual", UserId, Clock);

        var result = await svc.InspectAsync(plan, AllCells([SlotA]), null, null, [priorPlan], Today);

        Assert.Contains(result.Insights, i => i.Kind == InsightKind.RepetitionVsHistory);
    }

    [Fact(DisplayName = "Rule 4 — no prior plans provided → RepetitionVsHistory rule suppressed")]
    public async Task Rule4_RepetitionVsHistory_SuppressedWhenNoPriorPlans()
    {
        var recipeId = Guid.NewGuid();
        var svc = BuildService();

        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Recipe, recipeId, 2)], null, "manual", UserId, Clock);

        var result = await svc.InspectAsync(plan, AllCells([SlotA]), null, null, priorPlans: null, Today);

        Assert.DoesNotContain(result.Insights, i => i.Kind == InsightKind.RepetitionVsHistory);
    }

    // ── Rule 5: UnfilledSlot ──────────────────────────────────────────────────

    [Fact(DisplayName = "Rule 5 — some cells have no meal → UnfilledSlot callout with correct count")]
    public async Task Rule5_UnfilledSlot_AppearsWhenCellsAreEmpty()
    {
        var svc = BuildService();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        // Assign only 1 of 7 days
        plan.AssignNote(Monday, SlotA, "Takeout", null, "manual", UserId, Clock);

        var allCells = AllCells([SlotA]);
        var result = await svc.InspectAsync(plan, allCells, null, null, null, Today);

        var insight = Assert.Single(result.Insights, i => i.Kind == InsightKind.UnfilledSlot);
        Assert.Equal("info", insight.Tone);
        Assert.Contains("6 slot", insight.Title); // 7 total - 1 filled = 6 empty
    }

    [Fact(DisplayName = "Rule 5 — all cells filled → no UnfilledSlot callout")]
    public async Task Rule5_UnfilledSlot_NoCalloutWhenAllCellsFilled()
    {
        var svc = BuildService();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        // Fill all 7 days for SlotA
        for (int i = 0; i < 7; i++)
            plan.AssignNote(Monday.AddDays(i), SlotA, $"Note {i}", null, "manual", UserId, Clock);

        var allCells = AllCells([SlotA]);
        var result = await svc.InspectAsync(plan, allCells, null, null, null, Today);

        Assert.DoesNotContain(result.Insights, i => i.Kind == InsightKind.UnfilledSlot);
    }

    // ── L2 composition: all rules run via faked adapters ─────────────────────

    [Fact(DisplayName = "L2 composition — InspectAsync composes over both faked adapters and returns insights from multiple rules")]
    public async Task InspectAsync_Composition_AllRulesRunViaFakeAdapters()
    {
        // Arrange: expiring product + recipe that does NOT consume it
        var expiringProductId = Guid.NewGuid();
        var recipeId = Guid.NewGuid();
        var expiringReader = new FakeExpiringStockReader([expiringProductId]);
        var recipeReader = new FakeEnrichmentRecipeReader(recipeId,
            new RecipeDishEnrichment(80, null, false, HasExpiringIngredients: false));

        var svc = new PlanInsightsService(expiringReader, recipeReader);
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);

        // Assign the recipe twice (triggers RepetitionThisWeek)
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Recipe, recipeId, 2)], null, "manual", UserId, Clock);
        plan.AssignMeal(Monday.AddDays(1), SlotA, [new DishSpec(DishKind.Recipe, recipeId, 2)], null, "manual", UserId, Clock);

        // allCells has 7 × SlotA + 7 × SlotB = 14 total; only 2 are filled → 12 unfilled
        var allCells = AllCells([SlotA, SlotB]);

        var result = await svc.InspectAsync(
            plan, allCells,
            weekTotalCost: 100m, budgetTarget: 80m, // triggers OverBudget
            priorPlans: null,
            Today);

        // Should have UnusedExpiring (expiring product not used by planned recipe), OverBudget,
        // RepetitionThisWeek, and UnfilledSlot — all from faked adapters, no real DB.
        Assert.Contains(result.Insights, i => i.Kind == InsightKind.UnusedExpiring);
        Assert.Contains(result.Insights, i => i.Kind == InsightKind.OverBudget);
        Assert.Contains(result.Insights, i => i.Kind == InsightKind.RepetitionThisWeek);
        Assert.Contains(result.Insights, i => i.Kind == InsightKind.UnfilledSlot);
        Assert.False(result.IsClean);
    }

    // ── Clean state ───────────────────────────────────────────────────────────

    [Fact(DisplayName = "IsClean — empty plan with all cells filled and no expiring stock → clean")]
    public async Task IsClean_ReturnsTrueWhenNothingIsWrong()
    {
        var svc = BuildService(new FakeExpiringStockReader([]));
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        // Fill all 7 cells for SlotA
        for (int i = 0; i < 7; i++)
            plan.AssignNote(Monday.AddDays(i), SlotA, $"Note {i}", null, "manual", UserId, Clock);

        var allCells = AllCells([SlotA]);
        var result = await svc.InspectAsync(plan, allCells, null, null, null, Today);

        Assert.True(result.IsClean);
    }

    // ── Rule 1 suppression: today is forwarded to GetEnrichmentAsync ─────────

    /// <summary>
    /// Regression guard for the "today: default" bug: the enrichment reader must receive
    /// the real <c>today</c> parameter so <c>HasExpiringIngredients</c> reflects actual
    /// expiry proximity, not year 0001.
    /// </summary>
    [Fact(DisplayName = "Rule 1 — suppression: GetEnrichmentAsync receives today, not default; suppresses callout when HasExpiringIngredients is true")]
    public async Task Rule1_Suppression_EnrichmentCalledWithRealToday_SuppressesCallout()
    {
        var expiringProductId = Guid.NewGuid();
        var recipeId = Guid.NewGuid();
        var expiringReader = new FakeExpiringStockReader([expiringProductId]);

        // Spy reader that records the `today` value it was called with, and returns
        // HasExpiringIngredients=true only when today equals Today (not default).
        var spyReader = new TodaySpyRecipeReader(recipeId, expectedToday: Today, hasExpiring: true);
        var svc = BuildService(expiringReader, spyReader);

        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Recipe, recipeId, 2)], null, "manual", UserId, Clock);

        var result = await svc.InspectAsync(plan, AllCells([SlotA]), null, null, null, Today);

        // The enrichment reader received today=Today (not default), so HasExpiringIngredients=true,
        // which suppresses the UnusedExpiring callout.
        Assert.True(spyReader.WasCalledWithExpectedToday, "GetEnrichmentAsync was not called with the expected today value.");
        Assert.DoesNotContain(result.Insights, i => i.Kind == InsightKind.UnusedExpiring);
    }
}

// ── test doubles ──────────────────────────────────────────────────────────────

/// <summary>Fake <see cref="IMealPlanExpiringStockReader"/> that returns a fixed list of product IDs.</summary>
internal sealed class FakeExpiringStockReader(IReadOnlyList<Guid> productIds) : IMealPlanExpiringStockReader
{
    public Task<IReadOnlyList<Guid>> GetExpiringProductIdsAsync(DateOnly today, int withinDays, CancellationToken ct = default)
        => Task.FromResult(productIds);
}

/// <summary>Fake <see cref="IRecipeReadModel"/> that returns no enrichment for any recipe (default stub).</summary>
internal sealed class FakeNoOpRecipeReader : IRecipeReadModel
{
    public Task<RecipeReadModel?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<RecipeReadModel?>(null);

    public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string q, int max, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeReadModel>>([]);

    public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid id, int servings, DateOnly today, CancellationToken ct = default)
        => Task.FromResult<RecipeDishEnrichment?>(null);

    public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid id, int servings, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeMissingIngredient>>([]);
}

/// <summary>
/// Spy <see cref="IRecipeReadModel"/> that verifies <see cref="GetEnrichmentAsync"/> is called
/// with a specific <paramref name="expectedToday"/> value. Returns <paramref name="hasExpiring"/>
/// in <see cref="RecipeDishEnrichment.HasExpiringIngredients"/> only when called with the expected date.
/// </summary>
internal sealed class TodaySpyRecipeReader(Guid recipeId, DateOnly expectedToday, bool hasExpiring) : IRecipeReadModel
{
    public bool WasCalledWithExpectedToday { get; private set; }

    public Task<RecipeReadModel?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<RecipeReadModel?>(null);

    public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string q, int max, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeReadModel>>([]);

    public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid id, int servings, DateOnly today, CancellationToken ct = default)
    {
        if (id == recipeId && today == expectedToday)
        {
            WasCalledWithExpectedToday = true;
            return Task.FromResult<RecipeDishEnrichment?>(
                new RecipeDishEnrichment(100, null, false, HasExpiringIngredients: hasExpiring));
        }
        // Called with wrong date (e.g. default) — return no enrichment so suppression doesn't fire.
        return Task.FromResult<RecipeDishEnrichment?>(null);
    }

    public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid id, int servings, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeMissingIngredient>>([]);
}
