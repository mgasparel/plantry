namespace Plantry.Shopping.Application;

/// <summary>
/// Anti-corruption read port: Shopping's read model needs the day + meal-slot of a Meal Planning
/// slot to resolve MealPlan-source contribution labels ("for Mon dinner") on the shopping board's
/// attribution sub-line (plantry-jwyb). Defined here in Shopping.Application and implemented in the
/// Web layer (an adapter over <c>MealPlanningDbContext</c>), the same ACL pattern as
/// <see cref="IShoppingRecipeReader"/> (Shopping must NOT read Meal Planning's EF context directly —
/// ADR-002, ADR-010 <c>MP → SHOP</c>).
///
/// <para>
/// Resolution is needed for <c>ItemSource.MealPlan</c> contributions whose <c>SourceRef</c> is a
/// meal-plan slot/entry id (a <c>planned_meal</c> id, NOT a recipe id — ShoppingListItem.cs:163).
/// SourceRefs that do not resolve to a slot (deleted, foreign household, or a coarser whole-plan ref)
/// are silently omitted from the result so the caller can fall back to a generic "for your meal plan"
/// label. Equality on the SourceRef is the only operation performed here; no domain reach-in.
/// </para>
/// </summary>
public interface IShoppingMealPlanReader
{
    /// <summary>
    /// Resolves the day-of-week and meal-slot label for a set of meal-plan slot ids in one batch call.
    /// Slot ids not found in the household (deleted or belonging to another household) — or ids that are
    /// not slot ids at all — are silently omitted from the result dictionary (caller falls back).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ShoppingMealPlanSlot>> GetMealPlanSlotsAsync(
        IReadOnlyList<Guid> slotRefs,
        CancellationToken ct = default);
}

/// <summary>
/// The slice of a Meal Planning slot the shopping attribution label needs: which weekday the meal is
/// planned for and the meal-slot label (e.g. "Dinner"). Projected for the "for {Day} {meal}" label;
/// never persisted.
/// </summary>
/// <param name="Day">The weekday the slot is planned for (derived from the planned meal's date).</param>
/// <param name="MealType">The meal-slot label as configured by the household (e.g. "Dinner", "Lunch").</param>
public sealed record ShoppingMealPlanSlot(DayOfWeek Day, string MealType);
