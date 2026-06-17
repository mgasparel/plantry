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
///         C11 (MoveMeal relocates or swaps within week),
///         C12 (MoveMeal does NOT re-validate constraints).
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

    // ── C11 — MoveMeal: relocate onto empty cell ──────────────────────────────

    [Fact]
    public void MoveMeal_RelocatesMealToEmptyCell()
    {
        var plan = CreatePlan();
        plan.AssignMeal(Monday, SlotA, [RecipeDish()], null, "test", UserId, Clock);
        var originalMealId = plan.PlannedMeals[0].Id;

        var tuesday = Monday.AddDays(1);
        plan.MoveMeal(Monday, SlotA, tuesday, SlotB, Clock);

        var moved = plan.PlannedMeals.Single();
        Assert.Equal(originalMealId, moved.Id);
        Assert.Equal(tuesday, moved.Date);
        Assert.Equal(SlotB, moved.MealSlotId);
    }

    [Fact]
    public void MoveMeal_LeavesSourceCellEmpty_AfterRelocate()
    {
        var plan = CreatePlan();
        plan.AssignMeal(Monday, SlotA, [RecipeDish()], null, "test", UserId, Clock);

        var tuesday = Monday.AddDays(1);
        plan.MoveMeal(Monday, SlotA, tuesday, SlotB, Clock);

        Assert.Empty(plan.PlannedMeals.Where(m => m.Date == Monday && m.MealSlotId == SlotA));
    }

    // ── C11 — MoveMeal: swap with occupied cell ───────────────────────────────

    [Fact]
    public void MoveMeal_SwapsBothMeals_WhenTargetOccupied()
    {
        var plan = CreatePlan();
        var tuesday = Monday.AddDays(1);

        plan.AssignMeal(Monday, SlotA, [RecipeDish()], null, "test", UserId, Clock);
        plan.AssignMeal(tuesday, SlotB, [RecipeDish()], null, "test", UserId, Clock);

        var originalMoverMealId = plan.PlannedMeals.Single(m => m.Date == Monday && m.MealSlotId == SlotA).Id;
        var originalTargetMealId = plan.PlannedMeals.Single(m => m.Date == tuesday && m.MealSlotId == SlotB).Id;

        plan.MoveMeal(Monday, SlotA, tuesday, SlotB, Clock);

        var afterMover = plan.PlannedMeals.Single(m => m.Id == originalMoverMealId);
        var afterTarget = plan.PlannedMeals.Single(m => m.Id == originalTargetMealId);

        Assert.Equal(tuesday, afterMover.Date);
        Assert.Equal(SlotB, afterMover.MealSlotId);
        Assert.Equal(Monday, afterTarget.Date);
        Assert.Equal(SlotA, afterTarget.MealSlotId);
    }

    // ── M4 — override travels on MoveMeal ────────────────────────────────────

    [Fact]
    public void MoveMeal_PreservesAttendeesOverride_OnRelocation()
    {
        var plan = CreatePlan();
        var overrideList = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        plan.AssignMeal(Monday, SlotA, [RecipeDish()], overrideList, "test", UserId, Clock);

        var tuesday = Monday.AddDays(1);
        plan.MoveMeal(Monday, SlotA, tuesday, SlotA, Clock);

        var moved = plan.PlannedMeals.Single();
        Assert.Equal(overrideList, moved.AttendeesOverride);
    }

    [Fact]
    public void MoveMeal_SwapPreservesEachMealsOwnOverride()
    {
        var plan = CreatePlan();
        var tuesday = Monday.AddDays(1);
        var overrideA = new List<Guid> { Guid.NewGuid() };
        var overrideB = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };

        plan.AssignMeal(Monday, SlotA, [RecipeDish()], overrideA, "test", UserId, Clock);
        plan.AssignMeal(tuesday, SlotB, [RecipeDish()], overrideB, "test", UserId, Clock);

        plan.MoveMeal(Monday, SlotA, tuesday, SlotB, Clock);

        // Mover (originally at Monday/SlotA) now at tuesday/SlotB — still has overrideA
        var movedToTuesday = plan.PlannedMeals.Single(m => m.Date == tuesday && m.MealSlotId == SlotB);
        Assert.Equal(overrideA, movedToTuesday.AttendeesOverride);

        // Target (originally at tuesday/SlotB) now at Monday/SlotA — still has overrideB
        var movedToMonday = plan.PlannedMeals.Single(m => m.Date == Monday && m.MealSlotId == SlotA);
        Assert.Equal(overrideB, movedToMonday.AttendeesOverride);
    }

    // ── MoveMeal raises domain event ──────────────────────────────────────────

    [Fact]
    public void MoveMeal_RaisesMealMovedEvent()
    {
        var plan = CreatePlan();
        plan.AssignMeal(Monday, SlotA, [RecipeDish()], null, "test", UserId, Clock);
        plan.ClearDomainEvents(); // clear MealPlanned event

        var tuesday = Monday.AddDays(1);
        plan.MoveMeal(Monday, SlotA, tuesday, SlotB, Clock);

        var evt = Assert.IsType<MealMoved>(Assert.Single(plan.DomainEvents));
        Assert.Equal(Monday, evt.FromDate);
        Assert.Equal(SlotA, evt.FromSlotId);
        Assert.Equal(tuesday, evt.ToDate);
        Assert.Equal(SlotB, evt.ToSlotId);
        Assert.Null(evt.SwappedMealId);
    }

    [Fact]
    public void MoveMeal_SwapSetsSwappedMealId_InEvent()
    {
        var plan = CreatePlan();
        var tuesday = Monday.AddDays(1);
        plan.AssignMeal(Monday, SlotA, [RecipeDish()], null, "test", UserId, Clock);
        plan.AssignMeal(tuesday, SlotB, [RecipeDish()], null, "test", UserId, Clock);
        var targetId = plan.PlannedMeals.Single(m => m.Date == tuesday).Id;
        plan.ClearDomainEvents();

        plan.MoveMeal(Monday, SlotA, tuesday, SlotB, Clock);

        var evt = Assert.IsType<MealMoved>(Assert.Single(plan.DomainEvents));
        Assert.Equal(targetId, evt.SwappedMealId);
    }

    // ── ClearMeal ─────────────────────────────────────────────────────────────

    [Fact]
    public void ClearMeal_RemovesMeal_WhenPresent()
    {
        var plan = CreatePlan();
        plan.AssignMeal(Monday, SlotA, [RecipeDish()], null, "test", UserId, Clock);

        plan.ClearMeal(Monday, SlotA, Clock);

        Assert.Empty(plan.PlannedMeals);
    }

    [Fact]
    public void ClearMeal_IsNoOp_WhenCellEmpty()
    {
        var plan = CreatePlan();

        // Should not throw
        plan.ClearMeal(Monday, SlotA, Clock);
        Assert.Empty(plan.PlannedMeals);
    }

    // ── AssignMeal replaces existing ──────────────────────────────────────────

    [Fact]
    public void AssignMeal_ReplacesExistingMeal_InSameCell()
    {
        var plan = CreatePlan();
        plan.AssignMeal(Monday, SlotA, [RecipeDish()], null, "test", UserId, Clock);
        Assert.Single(plan.PlannedMeals);
        var originalId = plan.PlannedMeals[0].Id;

        // Re-assign to the same cell with different dishes
        plan.AssignMeal(Monday, SlotA, [RecipeDish(), RecipeDish()], null, "test", UserId, Clock);

        // Should still be one meal in that cell (updated, not duplicated)
        Assert.Single(plan.PlannedMeals);
        Assert.Equal(originalId, plan.PlannedMeals[0].Id);
        Assert.Equal(2, plan.PlannedMeals[0].PlannedDishes.Count);
    }
}
