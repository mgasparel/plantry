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

    /// <summary>
    /// Creates a new <see cref="PlannedMeal"/> with dishes. Use when the meal is dish-based (M13).
    /// Caller must supply at least one dish. Each dish must have servings >= 1.
    /// </summary>
    internal static PlannedMeal CreateWithDishes(
        HouseholdId householdId,
        MealPlanId mealPlanId,
        DateOnly date,
        MealSlotId slotId,
        IReadOnlyList<DishSpec> dishes,
        List<Guid>? attendeesOverride,
        string source,
        Guid createdBy,
        DateTimeOffset now)
    {
        if (dishes.Count == 0)
            throw new InvalidOperationException("At least one dish is required when creating a dish-based meal (M13).");

        var meal = new PlannedMeal
        {
            Id = PlannedMealId.New(),
            HouseholdId = householdId,
            MealPlanId = mealPlanId,
            Date = date,
            MealSlotId = slotId,
            AttendeesOverride = attendeesOverride,
            Note = null,
            Source = source,
            CreatedBy = createdBy,
            UpdatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
        };

        for (var i = 0; i < dishes.Count; i++)
        {
            var spec = dishes[i];
            meal._plannedDishes.Add(spec.Kind == DishKind.Recipe
                ? PlannedDish.CreateForRecipe(householdId, meal.Id, spec.ItemId, spec.Servings, ordinal: i + 1)
                : PlannedDish.CreateForProduct(householdId, meal.Id, spec.ItemId, spec.Servings, ordinal: i + 1));
        }

        return meal;
    }

    /// <summary>
    /// Creates a new <see cref="PlannedMeal"/> with a free-text note. Use when the meal is note-based (M13).
    /// </summary>
    internal static PlannedMeal CreateWithNote(
        HouseholdId householdId,
        MealPlanId mealPlanId,
        DateOnly date,
        MealSlotId slotId,
        string note,
        List<Guid>? attendeesOverride,
        string source,
        Guid createdBy,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(note))
            throw new InvalidOperationException("Note must not be blank when creating a note-based meal (M13).");

        return new PlannedMeal
        {
            Id = PlannedMealId.New(),
            HouseholdId = householdId,
            MealPlanId = mealPlanId,
            Date = date,
            MealSlotId = slotId,
            AttendeesOverride = attendeesOverride,
            Note = note.Trim(),
            Source = source,
            CreatedBy = createdBy,
            UpdatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Updates the dishes of this meal (wholesale replace). Validates M13 (dishes XOR note).</summary>
    internal void UpdateDishes(IReadOnlyList<DishSpec> dishes, Guid updatedBy, DateTimeOffset now)
    {
        if (dishes.Count == 0)
            throw new InvalidOperationException("At least one dish is required for a dish-based meal (M13).");

        Note = null;
        _plannedDishes.Clear();

        for (var i = 0; i < dishes.Count; i++)
        {
            var spec = dishes[i];
            _plannedDishes.Add(spec.Kind == DishKind.Recipe
                ? PlannedDish.CreateForRecipe(HouseholdId, Id, spec.ItemId, spec.Servings, ordinal: i + 1)
                : PlannedDish.CreateForProduct(HouseholdId, Id, spec.ItemId, spec.Servings, ordinal: i + 1));
        }

        UpdatedBy = updatedBy;
        UpdatedAt = now;
    }

    /// <summary>Updates the note of this meal (wholesale replace). Validates M13.</summary>
    internal void UpdateNote(string note, Guid updatedBy, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(note))
            throw new InvalidOperationException("Note must not be blank for a note-based meal (M13).");

        _plannedDishes.Clear();
        Note = note.Trim();
        UpdatedBy = updatedBy;
        UpdatedAt = now;
    }

    /// <summary>
    /// Sets or clears the per-instance attendees override (M4).
    /// Null = inherit slot default; empty list = explicitly nobody; non-empty = these members.
    /// </summary>
    internal void SetAttendeesOverride(List<Guid>? attendeesOverride, Guid updatedBy, DateTimeOffset now)
    {
        AttendeesOverride = attendeesOverride;
        UpdatedBy = updatedBy;
        UpdatedAt = now;
    }

    /// <summary>
    /// Moves this meal to a different cell within the same plan (M4: override travels).
    /// </summary>
    internal void MoveTo(DateOnly newDate, MealSlotId newSlotId, Guid updatedBy, DateTimeOffset now)
    {
        Date = newDate;
        MealSlotId = newSlotId;
        UpdatedBy = updatedBy;
        UpdatedAt = now;
    }
}

/// <summary>Specifies a single dish to include in a meal.</summary>
public sealed record DishSpec(DishKind Kind, Guid ItemId, int Servings);

public enum DishKind { Recipe, Product }
