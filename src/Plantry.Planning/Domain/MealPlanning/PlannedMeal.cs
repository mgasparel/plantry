using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Planning.Domain;

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
    /// Position within the cell's stack; 1-based, contiguous per (MealPlanId, Date, MealSlotId).
    /// New meals append at NextOrdinal (max+1, or 1 if empty). RenumberCell keeps it contiguous
    /// after removal.
    /// </summary>
    public int Ordinal { get; private set; }

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
        DateTimeOffset now,
        int ordinal = 1)
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
            Ordinal = ordinal,
            CreatedBy = createdBy,
            UpdatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
        };

        for (var i = 0; i < dishes.Count; i++)
        {
            var spec = dishes[i];
            meal._plannedDishes.Add(DishSpecExtensions.CreateDish(householdId, meal.Id, spec, i + 1));
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
        DateTimeOffset now,
        int ordinal = 1)
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
            Ordinal = ordinal,
            CreatedBy = createdBy,
            UpdatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Updates the dishes of this meal. Diffs against the existing dishes rather than replacing
    /// them wholesale: a new-spec dish is matched (by Kind + RecipeId/ProductId, greedy,
    /// oldest-ordinal-first, one match per existing dish, <b>monotonic</b> in old <see cref="Ordinal"/>
    /// — see below) against an existing <see cref="PlannedDish"/> and, when matched, that same
    /// instance is kept — preserving its <see cref="PlannedDishId"/> and therefore any cook/eat
    /// history recorded against it (CookEvent.PlannedDishId, Inventory journal SourceRef — see
    /// MealPlanCookStatusReaderAdapter). Its servings and ordinal are updated to the new spec, but
    /// a dish that was already cooked/eaten stays "done" — the status lookup only checks whether
    /// history exists for the id, not the servings value, which is the intended semantic ("this
    /// dish was cooked"). Only genuinely new dishes get a fresh <see cref="PlannedDish"/>; only
    /// genuinely removed dishes are dropped from the meal (their history remains, just no longer
    /// displayed — correct, since the dish is gone from the plan).
    ///
    /// <para>
    /// <b>Ordinal-uniqueness hazard.</b> <c>ux_planned_dish_meal_ordinal</c> is a plain UNIQUE
    /// index on (planned_meal_id, ordinal) and, unlike the meal-level slot constraint, is NOT
    /// deferrable — migration 20260617143350 made the meal-level constraint deferrable
    /// specifically to allow in-place ordinal swaps, and 20260617180000 later dropped that
    /// deferability once the swap path it existed for was removed, so there is no deferrable
    /// fallback anywhere in this schema today. A true ordinal permutation (e.g. kept dish A moving
    /// from ordinal 1 to 2 while kept dish B moves from 2 to 1 in the same edit) would emit two
    /// in-place UPDATEs that collide against that index. The match below is therefore constrained
    /// to be monotonic in the old dishes' <see cref="Ordinal"/> — a kept dish can only match an old
    /// dish at or after the old dish consumed by the previous match — which guarantees kept dishes
    /// can only shift down (or stay), never invert relative order, so no swap is ever produced (a
    /// dish removed and later re-added becomes a brand-new <see cref="PlannedDish"/> instead, which
    /// is also the correct semantic — the user deleted it). If a dish-reorder affordance is ever
    /// added to the meal editor, or any other path that would insert a new dish ahead of a kept
    /// one, this monotonic guarantee no longer holds and the unique index needs to be replaced with
    /// <c>UNIQUE (planned_meal_id, ordinal) DEFERRABLE INITIALLY DEFERRED</c>, per the
    /// 20260617143350 precedent.
    /// </para>
    ///
    /// Validates M13 (dishes XOR note).
    /// </summary>
    internal void UpdateDishes(IReadOnlyList<DishSpec> dishes, Guid updatedBy, DateTimeOffset now)
    {
        if (dishes.Count == 0)
            throw new InvalidOperationException("At least one dish is required for a dish-based meal (M13).");

        Note = null;

        var candidates = _plannedDishes.OrderBy(d => d.Ordinal).ToList();
        var matchedOld = new HashSet<PlannedDish>();
        var added = new List<PlannedDish>(dishes.Count);
        var searchFrom = 0; // monotonic: never match an older dish that precedes an already-matched one

        for (var i = 0; i < dishes.Count; i++)
        {
            var spec = dishes[i];
            var matchIndex = -1;
            for (var j = searchFrom; j < candidates.Count; j++)
            {
                var old = candidates[j];
                var sameItem = spec.Kind == DishKind.Recipe
                    ? old.RecipeId == spec.ItemId
                    : old.ProductId == spec.ItemId;
                if (sameItem)
                {
                    matchIndex = j;
                    break;
                }
            }

            if (matchIndex >= 0)
            {
                var match = candidates[matchIndex];
                searchFrom = matchIndex + 1;
                matchedOld.Add(match);
                if (spec.Kind == DishKind.Recipe)
                    match.SetServings(spec.RequiredServings());
                else
                    match.SetProductQuantity(spec.RequiredQuantity(), spec.RequiredUnitId());
                match.SetOrdinal(i + 1);
            }
            else
            {
                added.Add(DishSpecExtensions.CreateDish(HouseholdId, Id, spec, i + 1));
            }
        }

        // Drop dishes that weren't matched (genuinely removed from the plan). Kept dishes stay in
        // place in the backing list (not removed/re-added) so EF's change tracker sees them as
        // in-place updates, not delete+insert.
        _plannedDishes.RemoveAll(d => !matchedOld.Contains(d));
        _plannedDishes.AddRange(added);

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
    /// Updates the ordinal of this meal within its cell's stack.
    /// Called by MealPlan.RenumberCell to keep ordinals contiguous after a removal.
    /// </summary>
    internal void SetOrdinal(int ordinal) => Ordinal = ordinal;

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

/// <summary>Specifies one explicitly shaped recipe or product dish.</summary>
public sealed record DishSpec
{
    public DishKind Kind { get; init; }
    public Guid ItemId { get; init; }
    public int? Servings { get; init; }
    public decimal? Quantity { get; init; }
    public Guid? UnitId { get; init; }

    // Positional construction is retained for recipe callers only; product callers must use the
    // explicit quantity + unit factory below so an implicit/default unit can never be persisted.
    public DishSpec(DishKind kind, Guid itemId, int servings)
    {
        if (kind != DishKind.Recipe)
            throw new ArgumentException("Product dishes require an explicit quantity and unit.", nameof(kind));
        Kind = kind;
        ItemId = itemId;
        Servings = servings;
    }

    public DishSpec(DishKind kind, Guid itemId, decimal quantity, Guid unitId)
    {
        if (kind != DishKind.Product) throw new ArgumentException("Decimal dishes must be products.", nameof(kind));
        if (quantity <= 0m) throw new ArgumentOutOfRangeException(nameof(quantity), "Product quantity must be > 0.");
        if (unitId == Guid.Empty) throw new ArgumentException("Product unit is required.", nameof(unitId));
        Kind = kind;
        ItemId = itemId;
        Quantity = quantity;
        UnitId = unitId;
    }

    public static DishSpec ForRecipe(Guid recipeId, int servings) => new(DishKind.Recipe, recipeId, servings);
    public static DishSpec ForProduct(Guid productId, decimal quantity, Guid unitId) =>
        new(DishKind.Product, productId, quantity, unitId);

    public int RequiredServings() => Servings is >= 1 ? Servings.Value : throw new ArgumentOutOfRangeException(nameof(Servings), "Recipe servings must be >= 1.");
    public decimal RequiredQuantity() => Quantity is > 0m ? Quantity.Value : throw new ArgumentOutOfRangeException(nameof(Quantity), "Product quantity must be > 0.");
    public Guid RequiredUnitId() => UnitId is { } id && id != Guid.Empty ? id : throw new ArgumentException("Product unit is required.", nameof(UnitId));
}

public enum DishKind { Recipe, Product }

internal static class DishSpecExtensions
{
    internal static PlannedDish CreateDish(HouseholdId householdId, PlannedMealId mealId, DishSpec spec, int ordinal) =>
        spec.Kind == DishKind.Recipe
            ? PlannedDish.CreateForRecipe(householdId, mealId, spec.ItemId, spec.RequiredServings(), ordinal)
            : PlannedDish.CreateForProduct(householdId, mealId, spec.ItemId, spec.RequiredQuantity(), spec.RequiredUnitId(), ordinal);
}
