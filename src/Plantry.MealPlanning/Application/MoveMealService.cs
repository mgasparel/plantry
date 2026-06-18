using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.MealPlanning.Application;

/// <summary>
/// Application service for relocating a meal within a week (J9 / MP-O8).
/// Drag-drop relocates the meal into the target cell's stack — there is no swap.
/// Does NOT re-validate constraints (C12).
/// </summary>
public sealed class MoveMealService(
    IMealPlanRepository mealPlanRepo,
    IClock clock)
{
    /// <summary>
    /// Moves the meal identified by <paramref name="mealId"/> to the target cell.
    /// The meal is appended at NextOrdinal in the target cell; the source cell is renumbered.
    /// Cross-week moves are not supported (C11).
    /// </summary>
    public async Task MoveAsync(
        HouseholdId householdId,
        PlannedMealId mealId,
        DateOnly toDate,
        MealSlotId toSlotId,
        CancellationToken ct = default)
    {
        var plan = await mealPlanRepo.FindByWeekAsync(householdId, MealPlan.NormalizeToMonday(toDate), ct)
            ?? throw new InvalidOperationException($"No meal plan found for week containing {toDate}.");

        var mover = plan.FindById(mealId)
            ?? throw new InvalidOperationException($"No meal with id {mealId} to move.");

        var fromWeek = MealPlan.NormalizeToMonday(mover.Date);
        var toWeek = MealPlan.NormalizeToMonday(toDate);

        if (fromWeek != toWeek)
            throw new InvalidOperationException(
                "MoveMeal can only relocate within the same week (C11). Cross-week moves are not supported.");

        plan.MoveMeal(mealId, toDate, toSlotId, clock);
        await mealPlanRepo.SaveChangesAsync(ct);
    }
}
