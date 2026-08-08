using Plantry.Planning.Domain;
using Plantry.SharedKernel;

namespace Plantry.Planning.Application;

/// <summary>
/// Counts a household's consecutive weekly meal-planning streak (plantry-h9z9, Today streak-chip
/// injection point) — the number of weeks in a row, ending at the most recently completed week, that
/// have at least one <see cref="MealPlan.PlannedMeals"/> entry. Fetches every planned week-start (at or
/// before this week) in <b>one</b> lean, scalar-only query
/// (<see cref="IMealPlanRepository.PlannedWeekStartsBeforeAsync"/>) and walks the result in memory —
/// deliberately not a per-week <see cref="IMealPlanRepository.FindByWeekAsync"/> loop, which would cost
/// one full meal/dish aggregate load per week of streak on every <c>/Today</c> render.
///
/// <para><b>Interpretation (plantry-h9z9, minor ambiguity):</b> the current, possibly still-open week is
/// never allowed to zero out an otherwise-intact streak just because it has no plan yet — a Monday-morning
/// visit to Today shouldn't read "0-week streak" when last week (and every week before it) was planned. So
/// the walk always checks backward from <i>last</i> week regardless; the current week is checked separately
/// and, if it already has a plan, adds one on top (an early-bird bonus, not a requirement).</para>
/// </summary>
public sealed class MealPlanStreakQuery(IMealPlanRepository mealPlans)
{
    /// <summary>
    /// Hard cap on how many weeks the streak walk will consider (~2 years) — a safety bound so an
    /// implausibly long streak can never turn this into an unbounded scan.
    /// </summary>
    internal const int MaxWeeksScanned = 104;

    public async Task<int> ExecuteAsync(HouseholdId householdId, DateOnly today, CancellationToken ct = default)
    {
        var currentWeek = MealPlan.NormalizeToMonday(today);

        // +1 so the current week's own slot (checked separately below) doesn't eat into the budget the
        // backward walk needs for MaxWeeksScanned prior weeks.
        var plannedWeeks = await mealPlans.PlannedWeekStartsBeforeAsync(
            householdId, currentWeek, MaxWeeksScanned + 1, ct);

        var streak = 0;
        var index = 0;
        var expected = currentWeek;

        // Current-week bonus: only counts if it's exactly the first (most recent) row — never required.
        if (index < plannedWeeks.Count && plannedWeeks[index] == expected)
        {
            streak++;
            index++;
        }
        expected = expected.AddDays(-7);

        // Backward walk over the remaining descending rows: each must match the next expected consecutive
        // Monday exactly — any gap (a week absent from the planned list) stops the count immediately.
        while (index < plannedWeeks.Count && plannedWeeks[index] == expected)
        {
            streak++;
            index++;
            expected = expected.AddDays(-7);
        }

        return streak;
    }
}
