using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Planning.Domain;

/// <summary>
/// Repository port for the <see cref="MealPlan"/> aggregate.
/// Implemented in <c>Plantry.Planning.Infrastructure</c>.
/// </summary>
public interface IMealPlanRepository
{
    /// <summary>Finds the plan for the given household and week start (Monday). Returns null if none exists yet.</summary>
    Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default);

    /// <summary>
    /// Finds the plan for the given household and week start, or creates and tracks a new empty one.
    /// The caller must call <see cref="SaveChangesAsync"/> to persist a newly-created plan.
    /// </summary>
    Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default);

    /// <summary>
    /// Resolves the day-of-week and meal-slot label for a set of <c>planned_meal</c> slot ids in one
    /// batch call — the read Shopping's attribution labels need to render "for {Day} {meal}" on a
    /// MealPlan-source contribution (plantry-jwyb). Slot ids not found in the household (deleted, a
    /// coarser whole-plan ref, or belonging to another household) are silently omitted from the result
    /// so the caller can fall back to a generic "for your meal plan" label.
    /// <para>
    /// Formerly the MealPlanning→Shopping ACL read port <c>IShoppingMealPlanReader</c> (implemented by
    /// <c>ShoppingMealPlanReaderAdapter</c> over <c>MealPlanningDbContext</c> directly) — collapsed to an
    /// intra-context repository method now that both halves live in Plantry.Planning (ADR-024,
    /// plantry-g3da.5), mirroring how the Market merge folded <c>RecordDealObservationAdapter</c> into
    /// <c>ConfirmDeal</c> calling <c>RecordObservationCommand</c> in-process.
    /// </para>
    /// </summary>
    Task<IReadOnlyDictionary<Guid, PlannedMealSlotInfo>> FindSlotLabelsAsync(
        IReadOnlyList<Guid> plannedMealIds, CancellationToken ct = default);

    /// <summary>
    /// Distinct week-start (Monday) dates, for which the household has at least one planned meal,
    /// found while scanning backward one week at a time starting at <paramref name="notAfter"/> for at
    /// most <paramref name="maxWeeks"/> calendar weeks — descending (most recent first). Feeds
    /// <see cref="Plantry.Planning.Application.MealPlanStreakQuery"/>'s consecutive-week streak walk
    /// (plantry-h9z9): since that walk only ever consumes the leading contiguous run (it stops at the
    /// first week absent from this list), "scanned <paramref name="maxWeeks"/> calendar weeks" and
    /// "returned the <paramref name="maxWeeks"/> most recent planned weeks regardless of gaps" are
    /// equivalent for its purposes — this contract picks the cheaper one.
    /// <para>The default implementation falls back to a per-week <see cref="FindByWeekAsync"/> loop so
    /// test doubles need not reimplement it (the same "default falls back to an existing simple method,
    /// the real EF repository overrides with a batched query" convention Inventory's
    /// <c>IProductStockRepository.ListProductIdsWithStockAsync</c> already established); the EF
    /// repository overrides it with one scalar-only query that never materializes a meal/dish graph.</para>
    /// </summary>
    async Task<IReadOnlyList<DateOnly>> PlannedWeekStartsBeforeAsync(
        HouseholdId householdId, DateOnly notAfter, int maxWeeks, CancellationToken ct = default)
    {
        var result = new List<DateOnly>();
        var week = notAfter;
        for (var i = 0; i < maxWeeks; i++)
        {
            var plan = await FindByWeekAsync(householdId, week, ct);
            if (plan is { PlannedMeals.Count: > 0 })
                result.Add(week);
            week = week.AddDays(-7);
        }
        return result;
    }

    Task SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>
/// The slice of a <c>planned_meal</c> slot Shopping's attribution label needs: which weekday the meal is
/// planned for and the meal-slot label (e.g. "Dinner"). Projected for the "for {Day} {meal}" label;
/// never persisted.
/// </summary>
/// <param name="Day">The weekday the slot is planned for (derived from the planned meal's date).</param>
/// <param name="MealType">The meal-slot label as configured by the household (e.g. "Dinner", "Lunch").</param>
public sealed record PlannedMealSlotInfo(DayOfWeek Day, string MealType);
