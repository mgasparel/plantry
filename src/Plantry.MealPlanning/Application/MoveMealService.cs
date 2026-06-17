using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.MealPlanning.Application;

/// <summary>
/// Application service for relocating or swapping meals within a week (J9).
/// Orchestrates the MealPlan.MoveMeal domain operation.
/// Does NOT re-validate constraints (C12).
/// </summary>
public sealed class MoveMealService(
    IMealPlanRepository mealPlanRepo,
    IClock clock)
{
    /// <summary>
    /// Moves a planned meal from one cell to another within the same week.
    /// If the target cell is occupied, the two meals are swapped (C11).
    /// The per-instance AttendeesOverride travels with each meal (M4).
    /// No constraint re-validation (C12).
    /// </summary>
    public async Task MoveAsync(
        HouseholdId householdId,
        DateOnly fromDate,
        MealSlotId fromSlotId,
        DateOnly toDate,
        MealSlotId toSlotId,
        CancellationToken ct = default)
    {
        var fromWeek = MealPlan.NormalizeToMonday(fromDate);
        var toWeek = MealPlan.NormalizeToMonday(toDate);

        if (fromWeek != toWeek)
            throw new InvalidOperationException(
                "MoveMeal can only relocate within the same week (C11). Cross-week moves are not supported.");

        var plan = await mealPlanRepo.FindByWeekAsync(householdId, fromWeek, ct)
            ?? throw new InvalidOperationException($"No meal plan found for week {fromWeek}.");

        // Check whether the target cell is occupied before mutating the domain model.
        // For a swap (target occupied), EF Core's change tracker raises a circular-
        // dependency exception on the unique index (meal_plan_id, date, meal_slot_id)
        // if both entities are updated via SaveChangesAsync — it detects that row A
        // wants row B's slot while row B still holds it, and vice versa. We bypass this
        // by delegating the swap to SwapMealPositionsAsync, which issues two raw UPDATEs
        // inside an explicit transaction and then detaches both rows from the tracker.
        var mover = plan.FindMealPublic(fromDate, fromSlotId)
            ?? throw new InvalidOperationException($"No meal at ({fromDate}, {fromSlotId}) to move.");
        var target = plan.FindMealPublic(toDate, toSlotId);

        if (target is null)
        {
            // Simple relocate — no constraint cycle risk.
            plan.MoveMeal(fromDate, fromSlotId, toDate, toSlotId, clock);
            await mealPlanRepo.SaveChangesAsync(ct);
        }
        else
        {
            // Swap: issue two raw UPDATEs in one transaction bypassing EF change tracking.
            var now = clock.UtcNow;
            var updatedBy = mover.UpdatedBy;
            await mealPlanRepo.SwapMealPositionsAsync(
                mover.Id, toDate, toSlotId,
                target.Id, fromDate, fromSlotId,
                updatedBy, now, ct);
            // Raise domain events via the plan (no SaveChangesAsync needed — raw SQL committed above).
            plan.RecordSwap(mover.Id, fromDate, fromSlotId, target.Id, toDate, toSlotId);
        }
    }
}
