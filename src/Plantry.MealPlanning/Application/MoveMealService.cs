using Microsoft.Extensions.Logging;
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
    IClock clock,
    ILogger<MoveMealService> logger)
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
        var plan = await mealPlanRepo.FindByWeekAsync(householdId, MealPlan.NormalizeToMonday(toDate), ct);
        if (plan is null)
        {
            logger.LogWarning(
                "MoveMeal failed — no meal plan for week containing {ToDate} in household {HouseholdId}.",
                toDate, householdId.Value);
            throw new InvalidOperationException($"No meal plan found for week containing {toDate}.");
        }

        var mover = plan.FindById(mealId);
        if (mover is null)
        {
            logger.LogWarning(
                "MoveMeal failed — meal {MealId} not found in plan for week containing {ToDate}.",
                mealId.Value, toDate);
            throw new InvalidOperationException($"No meal with id {mealId} to move.");
        }

        var fromWeek = MealPlan.NormalizeToMonday(mover.Date);
        var toWeek = MealPlan.NormalizeToMonday(toDate);

        if (fromWeek != toWeek)
        {
            logger.LogWarning(
                "MoveMeal rejected — cross-week move attempted for meal {MealId} from {FromWeek} to {ToWeek}.",
                mealId.Value, fromWeek, toWeek);
            throw new InvalidOperationException(
                "MoveMeal can only relocate within the same week (C11). Cross-week moves are not supported.");
        }

        plan.MoveMeal(mealId, toDate, toSlotId, clock);
        await mealPlanRepo.SaveChangesAsync(ct);
        logger.LogInformation(
            "Meal {MealId} moved to {ToDate}/{ToSlotId}.", mealId.Value, toDate, toSlotId.Value);
    }
}
