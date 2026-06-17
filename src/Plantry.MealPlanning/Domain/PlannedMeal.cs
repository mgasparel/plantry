using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Entity child of <see cref="MealPlan"/>. Represents one occupied cell at a (date, slot)
/// in the week grid (mealplanning.md §planned_meal, C16).
/// Invariants enforced in application services: date-in-week (M2), dishes-XOR-note (M13).
/// </summary>
public sealed class PlannedMeal : Entity<PlannedMealId>
{
    private readonly List<PlannedDish> _plannedDishes = [];

    // Required by EF
    private PlannedMeal() { }

    public HouseholdId HouseholdId { get; private set; }
    public MealPlanId MealPlanId { get; private set; }
    public DateOnly Date { get; private set; }
    public MealSlotId MealSlotId { get; private set; }

    /// <summary>
    /// NULL = inherit slot's default_attendees; empty list = explicitly nobody;
    /// non-empty = these members (M4).
    /// </summary>
    public List<Guid>? AttendeesOverride { get; private set; }

    /// <summary>AI snippet when this meal came from a proposal; null when hand-assigned.</summary>
    public string? Reasoning { get; private set; }

    /// <summary>Free-text occupied-slot marker ("Takeout"). Set XOR no planned_dish rows (M13).</summary>
    public string? Note { get; private set; }

    /// <summary>'manual' | 'ai' — provenance (mealplanning.md resolved call 4).</summary>
    public string Source { get; private set; } = default!;

    public Guid CreatedBy { get; private set; }
    public Guid UpdatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<PlannedDish> PlannedDishes => _plannedDishes.AsReadOnly();
}
