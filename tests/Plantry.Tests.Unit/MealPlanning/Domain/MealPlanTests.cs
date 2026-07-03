using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Xunit;

namespace Plantry.Tests.Unit.MealPlanning.Domain;

/// <summary>
/// L1 unit tests for <see cref="MealPlan"/> aggregate invariants.
/// Covers: M2 (date in week), M3 (servings >= 1), M4 (override travels on MoveMeal),
///         M8 (Monday normalization), M13 (dishes XOR note),
///         C9 (hard-stance warns, never blocks),
///         C11 (MoveMeal relocates into target stack, no swap — MP-O8),
///         C12 (MoveMeal does NOT re-validate constraints),
///         MP-O8 (stack: append, edit-by-id, clear-by-id+renumber, ordinal contiguity).
/// </summary>
public sealed class MealPlanTests
{
    private static readonly HouseholdId HouseholdId = HouseholdId.New();
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly DateOnly Monday = new(2026, 6, 1); // a known Monday
    private static readonly MealSlotId SlotA = MealSlotId.New();
    private static readonly MealSlotId SlotB = MealSlotId.New();
    private static readonly Guid UserId = Guid.NewGuid();

    private static MealPlan CreatePlan(DateOnly? weekDay = null)
        => MealPlan.Start(HouseholdId, weekDay ?? Monday, Clock);

    private static DishSpec RecipeDish(int servings = 2) =>
        new(DishKind.Recipe, Guid.NewGuid(), servings);

    // ── M8 — Monday normalization ─────────────────────────────────────────────

    [Fact]
    public void Start_NormalizesToMonday_WhenMidweekDateProvided()
    {
        var wednesday = Monday.AddDays(2);
        var plan = MealPlan.Start(HouseholdId, wednesday, Clock);

        Assert.Equal(Monday, plan.WeekStart);
    }

    [Fact]
    public void NormalizeToMonday_ReturnsMonday_ForAnyDayOfWeek()
    {
        foreach (var offset in Enumerable.Range(0, 7))
        {
            var day = Monday.AddDays(offset);
            Assert.Equal(Monday, MealPlan.NormalizeToMonday(day));
        }
    }

    // ── M2 — date in week ─────────────────────────────────────────────────────

    [Fact]
    public void AssignMeal_ThrowsForDateBeforeWeekStart()
    {
        var plan = CreatePlan();
        var sunday = Monday.AddDays(-1);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            plan.AssignMeal(sunday, SlotA, [RecipeDish()], null, "test", UserId, Clock));

        Assert.Contains("M2", ex.Message);
    }

    [Fact]
    public void AssignMeal_ThrowsForDateAfterWeekEnd()
    {
        var plan = CreatePlan();
        var nextMonday = Monday.AddDays(7);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            plan.AssignMeal(nextMonday, SlotA, [RecipeDish()], null, "test", UserId, Clock));

        Assert.Contains("M2", ex.Message);
    }

    [Fact]
    public void AssignMeal_AcceptsAllSevenDaysOfWeek()
    {
        var plan = CreatePlan();

        for (int i = 0; i < 7; i++)
        {
            var day = Monday.AddDays(i);
            var sid = MealSlotId.New();
            var result = plan.AssignMeal(day, sid, [RecipeDish()], null, "test", UserId, Clock);
            Assert.Null(result.HardStanceWarning);
        }

        Assert.Equal(7, plan.PlannedMeals.Count);
    }

    // ── M3 — servings >= 1 ───────────────────────────────────────────────────

    [Fact]
    public void AssignMeal_ThrowsForZeroServings()
    {
        var plan = CreatePlan();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            plan.AssignMeal(Monday, SlotA, [RecipeDish(0)], null, "test", UserId, Clock));
    }

    [Fact]
    public void AssignMeal_ThrowsForNegativeServings()
    {
        var plan = CreatePlan();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            plan.AssignMeal(Monday, SlotA, [RecipeDish(-1)], null, "test", UserId, Clock));
    }

    [Fact]
    public void AssignMeal_AcceptsServingsOf1()
    {
        var plan = CreatePlan();

        var result = plan.AssignMeal(Monday, SlotA, [RecipeDish(1)], null, "test", UserId, Clock);
        Assert.Null(result.HardStanceWarning);
    }

    // ── M13 — dishes XOR note ─────────────────────────────────────────────────

    [Fact]
    public void AssignMeal_ThrowsForEmptyDishList()
    {
        var plan = CreatePlan();

        Assert.Throws<InvalidOperationException>(() =>
            plan.AssignMeal(Monday, SlotA, [], null, "test", UserId, Clock));
    }

    [Fact]
    public void AssignNote_ThrowsForBlankNote()
    {
        var plan = CreatePlan();

        Assert.Throws<InvalidOperationException>(() =>
            plan.AssignNote(Monday, SlotA, "  ", null, "test", UserId, Clock));
    }

    [Fact]
    public void AssignNote_ThrowsForEmptyNote()
    {
        var plan = CreatePlan();

        Assert.Throws<InvalidOperationException>(() =>
            plan.AssignNote(Monday, SlotA, "", null, "test", UserId, Clock));
    }

    // ── C9 — hard-stance warning, never blocks ────────────────────────────────

    [Fact]
    public void AssignMeal_WithHardStanceWarning_DoesNotThrow_AndReturnsWarning()
    {
        var plan = CreatePlan();

        var result = plan.AssignMeal(Monday, SlotA, [RecipeDish()], null, "test", UserId, Clock,
            hardStanceWarning: "Contains peanuts (Restricted by Alice)");

        Assert.NotNull(result.HardStanceWarning);
        Assert.Contains("peanuts", result.HardStanceWarning);
        Assert.Single(plan.PlannedMeals);
    }

    // ── MP-O8 — cell stack: append second meal ────────────────────────────────

    [Fact]
    public void AssignMeal_AppendsTwoMealsToSameCell_WhenCalledTwiceWithoutMealId()
    {
        var plan = CreatePlan();

        plan.AssignMeal(Monday, SlotA, [RecipeDish()], null, "test", UserId, Clock);
        plan.AssignMeal(Monday, SlotA, [RecipeDish()], null, "test", UserId, Clock);

        Assert.Equal(2, plan.PlannedMeals.Count);
        var cellMeals = plan.MealsInCell(Monday, SlotA);
        Assert.Equal(2, cellMeals.Count);
        // Ordinals must be contiguous 1..n
        Assert.Equal(1, cellMeals[0].Ordinal);
        Assert.Equal(2, cellMeals[1].Ordinal);
    }

    // ── MP-O8 — edit by mealId ────────────────────────────────────────────────

    [Fact]
    public void AssignMeal_UpdatesOnlyTargetMeal_WhenMealIdSupplied()
    {
        var plan = CreatePlan();
        plan.AssignMeal(Monday, SlotA, [RecipeDish()], null, "test", UserId, Clock);
        plan.AssignMeal(Monday, SlotA, [RecipeDish()], null, "test", UserId, Clock);

        var firstId = plan.MealsInCell(Monday, SlotA)[0].Id;
        var secondId = plan.MealsInCell(Monday, SlotA)[1].Id;

        // Edit only the first meal
        var newDishes = new[] { RecipeDish(), RecipeDish() };
        plan.AssignMeal(Monday, SlotA, newDishes, null, "test", UserId, Clock, mealId: firstId);

        // Two meals still in cell
        var after = plan.MealsInCell(Monday, SlotA);
        Assert.Equal(2, after.Count);
        Assert.Equal(firstId, after[0].Id);
        Assert.Equal(2, after[0].PlannedDishes.Count);
        // Second meal unchanged
        Assert.Equal(secondId, after[1].Id);
    }

    // ── MP-O8 — clear by mealId + renumber ───────────────────────────────────

    [Fact]
    public void ClearMeal_RemovesOnlyTargetMeal_AndRenumbersCell()
    {
        var plan = CreatePlan();
        plan.AssignMeal(Monday, SlotA, [RecipeDish()], null, "test", UserId, Clock); // ordinal 1
        plan.AssignMeal(Monday, SlotA, [RecipeDish()], null, "test", UserId, Clock); // ordinal 2
        plan.AssignMeal(Monday, SlotA, [RecipeDish()], null, "test", UserId, Clock); // ordinal 3

        var firstId = plan.MealsInCell(Monday, SlotA)[0].Id;

        // Remove the first meal (ordinal 1)
        plan.ClearMeal(firstId, Clock);

        var remaining = plan.MealsInCell(Monday, SlotA);
        Assert.Equal(2, remaining.Count);
        // Ordinals must be renumbered to 1, 2
        Assert.Equal(1, remaining[0].Ordinal);
        Assert.Equal(2, remaining[1].Ordinal);
    }

    [Fact]
    public void ClearMeal_IsNoOp_WhenMealIdNotFound()
    {
        var plan = CreatePlan();
        plan.AssignMeal(Monday, SlotA, [RecipeDish()], null, "test", UserId, Clock);

        // Should not throw
        plan.ClearMeal(PlannedMealId.New(), Clock);
        Assert.Single(plan.PlannedMeals);
    }

    // ── C11 — MoveMeal: relocate onto empty cell ──────────────────────────────

    [Fact]
    public void MoveMeal_RelocatesMealToEmptyCell()
    {
        var plan = CreatePlan();
        plan.AssignMeal(Monday, SlotA, [RecipeDish()], null, "test", UserId, Clock);
        var mealId = plan.PlannedMeals[0].Id;

        var tuesday = Monday.AddDays(1);
        plan.MoveMeal(mealId, tuesday, SlotB, Clock);

        var moved = plan.PlannedMeals.Single();
        Assert.Equal(mealId, moved.Id);
        Assert.Equal(tuesday, moved.Date);
        Assert.Equal(SlotB, moved.MealSlotId);
    }

    [Fact]
    public void MoveMeal_LeavesSourceCellEmpty_AfterRelocate()
    {
        var plan = CreatePlan();
        plan.AssignMeal(Monday, SlotA, [RecipeDish()], null, "test", UserId, Clock);
        var mealId = plan.PlannedMeals[0].Id;

        var tuesday = Monday.AddDays(1);
        plan.MoveMeal(mealId, tuesday, SlotB, Clock);

        Assert.DoesNotContain(plan.PlannedMeals, m => m.Date == Monday && m.MealSlotId == SlotA);
    }

    // ── C11 — MoveMeal: relocate-into-occupied (append, no swap) ─────────────

    [Fact]
    public void MoveMeal_AppendsToOccupiedCell_NoSwap()
    {
        var plan = CreatePlan();
        var tuesday = Monday.AddDays(1);

        plan.AssignMeal(Monday, SlotA, [RecipeDish()], null, "test", UserId, Clock);
        plan.AssignMeal(tuesday, SlotB, [RecipeDish()], null, "test", UserId, Clock);

        var moverId = plan.PlannedMeals.Single(m => m.Date == Monday && m.MealSlotId == SlotA).Id;
        var targetId = plan.PlannedMeals.Single(m => m.Date == tuesday && m.MealSlotId == SlotB).Id;

        // Move Monday/SlotA into tuesday/SlotB — should join stack, not swap
        plan.MoveMeal(moverId, tuesday, SlotB, Clock);

        // Both meals should now be in tuesday/SlotB
        var cell = plan.MealsInCell(tuesday, SlotB);
        Assert.Equal(2, cell.Count);
        Assert.Contains(cell, m => m.Id == targetId);
        Assert.Contains(cell, m => m.Id == moverId);

        // Source cell must be empty
        Assert.Empty(plan.MealsInCell(Monday, SlotA));

        // Ordinals must be contiguous
        Assert.Equal(1, cell.Min(m => m.Ordinal));
        Assert.Equal(2, cell.Max(m => m.Ordinal));
    }

    // ── M4 — override travels on MoveMeal ────────────────────────────────────

    [Fact]
    public void MoveMeal_PreservesAttendeesOverride_OnRelocation()
    {
        var plan = CreatePlan();
        var overrideList = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        plan.AssignMeal(Monday, SlotA, [RecipeDish()], overrideList, "test", UserId, Clock);
        var mealId = plan.PlannedMeals.Single().Id;

        var tuesday = Monday.AddDays(1);
        plan.MoveMeal(mealId, tuesday, SlotA, Clock);

        var moved = plan.PlannedMeals.Single();
        Assert.Equal(overrideList, moved.AttendeesOverride);
    }

    // ── MoveMeal raises domain event ──────────────────────────────────────────

    [Fact]
    public void MoveMeal_RaisesMealMovedEvent()
    {
        var plan = CreatePlan();
        plan.AssignMeal(Monday, SlotA, [RecipeDish()], null, "test", UserId, Clock);
        var mealId = plan.PlannedMeals[0].Id;
        plan.ClearDomainEvents(); // clear MealPlanned event

        var tuesday = Monday.AddDays(1);
        plan.MoveMeal(mealId, tuesday, SlotB, Clock);

        var evt = Assert.IsType<MealMoved>(Assert.Single(plan.DomainEvents));
        Assert.Equal(Monday, evt.FromDate);
        Assert.Equal(SlotA, evt.FromSlotId);
        Assert.Equal(tuesday, evt.ToDate);
        Assert.Equal(SlotB, evt.ToSlotId);
        Assert.Null(evt.SwappedMealId);
    }

    // ── ApplyProposal — skips occupied cells (MP-O8) ─────────────────────────

    [Fact]
    public void ApplyProposal_SkipsCell_WhenItAlreadyHasMeals()
    {
        var plan = CreatePlan();
        var recipeId = Guid.NewGuid();

        // Pre-fill Monday/SlotA
        plan.AssignMeal(Monday, SlotA, [RecipeDish()], null, "manual", UserId, Clock);

        var proposals = new List<ProposedMeal>
        {
            new(Monday, SlotA, [], [new ProposedDish(recipeId, 2, 1)], "AI reasoning")
        };

        var accepted = plan.ApplyProposal(proposals, UserId, Clock);

        // Should be 0 — the occupied cell was skipped
        Assert.Equal(0, accepted);
        // Still exactly 1 meal (the original)
        Assert.Single(plan.PlannedMeals);
    }

    [Fact]
    public void ApplyProposal_FillsEmptyCell()
    {
        var plan = CreatePlan();
        var recipeId = Guid.NewGuid();

        var proposals = new List<ProposedMeal>
        {
            new(Monday, SlotA, [], [new ProposedDish(recipeId, 2, 1)], "AI reasoning")
        };

        var accepted = plan.ApplyProposal(proposals, UserId, Clock);

        Assert.Equal(1, accepted);
        var meal = Assert.Single(plan.PlannedMeals);
        Assert.Equal("ai", meal.Source);
    }
}
