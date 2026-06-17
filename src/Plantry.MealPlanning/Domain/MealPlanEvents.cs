using Plantry.SharedKernel.Domain;

namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Domain event raised when a meal is assigned (created or updated) in a plan cell.
/// </summary>
public sealed class MealPlanned : IDomainEvent
{
    public MealPlanned(MealPlanId mealPlanId, PlannedMealId plannedMealId, DateOnly date, MealSlotId slotId)
    {
        MealPlanId = mealPlanId;
        PlannedMealId = plannedMealId;
        Date = date;
        SlotId = slotId;
    }

    public Guid EventId { get; } = Guid.CreateVersion7();
    public DateTimeOffset OccurredAt { get; } = DateTimeOffset.UtcNow;
    public MealPlanId MealPlanId { get; }
    public PlannedMealId PlannedMealId { get; }
    public DateOnly Date { get; }
    public MealSlotId SlotId { get; }
}

/// <summary>
/// Domain event raised when a planned meal is moved to a different cell.
/// </summary>
public sealed class MealMoved : IDomainEvent
{
    public MealMoved(
        MealPlanId mealPlanId,
        PlannedMealId movedMealId,
        DateOnly fromDate, MealSlotId fromSlotId,
        DateOnly toDate, MealSlotId toSlotId,
        PlannedMealId? swappedMealId = null)
    {
        MealPlanId = mealPlanId;
        MovedMealId = movedMealId;
        FromDate = fromDate;
        FromSlotId = fromSlotId;
        ToDate = toDate;
        ToSlotId = toSlotId;
        SwappedMealId = swappedMealId;
    }

    public Guid EventId { get; } = Guid.CreateVersion7();
    public DateTimeOffset OccurredAt { get; } = DateTimeOffset.UtcNow;
    public MealPlanId MealPlanId { get; }
    public PlannedMealId MovedMealId { get; }
    public DateOnly FromDate { get; }
    public MealSlotId FromSlotId { get; }
    public DateOnly ToDate { get; }
    public MealSlotId ToSlotId { get; }
    /// <summary>Set when the target cell was occupied and the two meals were swapped.</summary>
    public PlannedMealId? SwappedMealId { get; }
}
