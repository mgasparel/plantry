using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Repository port for the <see cref="MealPlan"/> aggregate.
/// Implemented in <c>Plantry.MealPlanning.Infrastructure</c>.
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

    Task SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Atomically swaps the (date, meal_slot_id) positions of two planned meals within the same meal plan.
    /// Uses raw SQL to bypass EF Core's circular-dependency detection on the unique index
    /// (meal_plan_id, date, meal_slot_id). Both rows are updated in a single transaction.
    /// </summary>
    Task SwapMealPositionsAsync(
        PlannedMealId mealAId, DateOnly newDateA, MealSlotId newSlotA,
        PlannedMealId mealBId, DateOnly newDateB, MealSlotId newSlotB,
        Guid updatedBy, DateTimeOffset now,
        CancellationToken ct = default);
}
