using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Xunit;

namespace Plantry.Tests.Unit.MealPlanning.Application;

/// <summary>
/// L2 unit tests for <see cref="MealPlanStreakQuery"/> (plantry-h9z9) using an in-memory,
/// per-week-keyed fake repository — no EF, no DB.
///
/// Covers the interpretation documented on the query's own doc comment: a still-open current week
/// with no plan yet must not zero out an otherwise-intact streak (it's checked separately and only
/// ever adds, never subtracts), while a genuine gap anywhere in the backward walk stops the count at
/// that point regardless of what's further back.
/// </summary>
public sealed class MealPlanStreakQueryTests
{
    private static readonly HouseholdId HouseholdId = HouseholdId.New();
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly MealSlotId SlotA = MealSlotId.New();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly ThisMonday = new(2026, 6, 15); // a known Monday

    private static DishSpec RecipeDish() => new(DishKind.Recipe, Guid.NewGuid(), 2);

    /// <summary>Builds a planned (non-empty) MealPlan for the given Monday.</summary>
    private static MealPlan PlannedWeek(DateOnly monday)
    {
        var plan = MealPlan.Start(HouseholdId, monday, Clock);
        plan.AssignMeal(monday, SlotA, [RecipeDish()], null, "test", UserId, Clock);
        return plan;
    }

    [Fact(DisplayName = "No plans at all → streak is 0")]
    public async Task NoPlans_StreakIsZero()
    {
        var query = new MealPlanStreakQuery(new FakeWeeklyMealPlanRepository(new Dictionary<DateOnly, MealPlan>()));

        var streak = await query.ExecuteAsync(HouseholdId, ThisMonday.AddDays(3));

        Assert.Equal(0, streak);
    }

    [Fact(DisplayName = "Current week planned, no history → streak is 1")]
    public async Task OnlyCurrentWeekPlanned_StreakIsOne()
    {
        var plans = new Dictionary<DateOnly, MealPlan> { [ThisMonday] = PlannedWeek(ThisMonday) };
        var query = new MealPlanStreakQuery(new FakeWeeklyMealPlanRepository(plans));

        var streak = await query.ExecuteAsync(HouseholdId, ThisMonday.AddDays(3));

        Assert.Equal(1, streak);
    }

    [Fact(DisplayName = "Current week empty but the prior 3 weeks are planned → streak is 3, not 0")]
    public async Task CurrentWeekEmpty_PriorWeeksPlanned_StreakCountsPriorWeeksOnly()
    {
        var week1 = ThisMonday.AddDays(-7);
        var week2 = ThisMonday.AddDays(-14);
        var week3 = ThisMonday.AddDays(-21);
        var plans = new Dictionary<DateOnly, MealPlan>
        {
            [week1] = PlannedWeek(week1),
            [week2] = PlannedWeek(week2),
            [week3] = PlannedWeek(week3),
            // week4 (28 days back) deliberately absent — the streak boundary.
        };
        var query = new MealPlanStreakQuery(new FakeWeeklyMealPlanRepository(plans));

        // today falls in ThisMonday's week, which has no plan yet.
        var streak = await query.ExecuteAsync(HouseholdId, ThisMonday.AddDays(3));

        Assert.Equal(3, streak);
    }

    [Fact(DisplayName = "Current week planned on top of an intact prior streak → adds one on top")]
    public async Task CurrentWeekPlanned_OnTopOfPriorStreak_AddsOne()
    {
        var week1 = ThisMonday.AddDays(-7);
        var week2 = ThisMonday.AddDays(-14);
        var plans = new Dictionary<DateOnly, MealPlan>
        {
            [ThisMonday] = PlannedWeek(ThisMonday),
            [week1] = PlannedWeek(week1),
            [week2] = PlannedWeek(week2),
        };
        var query = new MealPlanStreakQuery(new FakeWeeklyMealPlanRepository(plans));

        var streak = await query.ExecuteAsync(HouseholdId, ThisMonday.AddDays(3));

        Assert.Equal(3, streak);
    }

    [Fact(DisplayName = "A gap stops the backward walk even if older weeks were planned")]
    public async Task GapInHistory_StopsCountAtTheGap()
    {
        var week1 = ThisMonday.AddDays(-7);   // planned
        var week2 = ThisMonday.AddDays(-14);  // gap — not planned
        var week3 = ThisMonday.AddDays(-21);  // planned, but unreachable past the gap
        var plans = new Dictionary<DateOnly, MealPlan>
        {
            [week1] = PlannedWeek(week1),
            [week3] = PlannedWeek(week3),
        };
        var query = new MealPlanStreakQuery(new FakeWeeklyMealPlanRepository(plans));

        var streak = await query.ExecuteAsync(HouseholdId, ThisMonday.AddDays(3));

        Assert.Equal(1, streak); // only week1 counts; week2's gap stops the walk before week3
    }

    /// <summary>A plan with zero PlannedMeals (created but never assigned) counts the same as no plan
    /// at all — <see cref="MealPlanStreakQuery"/> checks <c>PlannedMeals.Count > 0</c>, not mere
    /// existence of the aggregate.</summary>
    [Fact(DisplayName = "An empty MealPlan aggregate (no dishes assigned) does not count toward the streak")]
    public async Task EmptyMealPlanAggregate_DoesNotCount()
    {
        var emptyPlan = MealPlan.Start(HouseholdId, ThisMonday, Clock); // no AssignMeal call
        var plans = new Dictionary<DateOnly, MealPlan> { [ThisMonday] = emptyPlan };
        var query = new MealPlanStreakQuery(new FakeWeeklyMealPlanRepository(plans));

        var streak = await query.ExecuteAsync(HouseholdId, ThisMonday.AddDays(3));

        Assert.Equal(0, streak);
    }

    private sealed class FakeWeeklyMealPlanRepository(IReadOnlyDictionary<DateOnly, MealPlan> plans) : IMealPlanRepository
    {
        public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default) =>
            Task.FromResult(plans.TryGetValue(weekStart, out var plan) ? plan : null);

        public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default) =>
            throw new NotSupportedException("Not used by MealPlanStreakQuery.");

        public Task<IReadOnlyDictionary<Guid, PlannedMealSlotInfo>> FindSlotLabelsAsync(
            IReadOnlyList<Guid> plannedMealIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, PlannedMealSlotInfo>>(new Dictionary<Guid, PlannedMealSlotInfo>());

        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
