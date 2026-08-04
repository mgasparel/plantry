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
