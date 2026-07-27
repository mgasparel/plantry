using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Entity child of <see cref="PlannedMeal"/>. One dish (main, side, …) in a planned meal.
/// Exactly one of <see cref="RecipeId"/> / <see cref="ProductId"/> is set (M12 / num_nonnulls CHECK).
/// No timestamps. <see cref="Id"/> is load-bearing beyond this aggregate: cook/eat history
/// (CookEvent.PlannedDishId, Inventory journal SourceRef — see MealPlanCookStatusReaderAdapter)
/// is keyed to it, so <see cref="PlannedMeal.UpdateDishes"/> preserves the identity of any dish
/// that is still present after an edit rather than replacing it wholesale.
/// </summary>
public sealed class PlannedDish : Entity<PlannedDishId>
{
    // Required by EF
    private PlannedDish() { }

    public HouseholdId HouseholdId { get; private set; }
    public PlannedMealId PlannedMealId { get; private set; }

    /// <summary>Soft ref → recipes.recipe (DM-20). XOR <see cref="ProductId"/>.</summary>
    public Guid? RecipeId { get; private set; }

    /// <summary>Soft ref → catalog.product (DM-10). XOR <see cref="RecipeId"/>.</summary>
    public Guid? ProductId { get; private set; }

    /// <summary>Number of servings; >= 1 (M3).</summary>
    public int Servings { get; private set; }

    /// <summary>Position within the meal; UNIQUE (planned_meal_id, ordinal).</summary>
    public int Ordinal { get; private set; }

    internal static PlannedDish CreateForRecipe(
        HouseholdId householdId,
        PlannedMealId mealId,
        Guid recipeId,
        int servings,
        int ordinal)
    {
        if (servings < 1) throw new ArgumentOutOfRangeException(nameof(servings), "Servings must be >= 1 (M3).");
        return new PlannedDish
        {
            Id = PlannedDishId.New(),
            HouseholdId = householdId,
            PlannedMealId = mealId,
            RecipeId = recipeId,
            ProductId = null,
            Servings = servings,
            Ordinal = ordinal,
        };
    }

    internal static PlannedDish CreateForProduct(
        HouseholdId householdId,
        PlannedMealId mealId,
        Guid productId,
        int servings,
        int ordinal)
    {
        if (servings < 1) throw new ArgumentOutOfRangeException(nameof(servings), "Servings must be >= 1 (M3).");
        return new PlannedDish
        {
            Id = PlannedDishId.New(),
            HouseholdId = householdId,
            PlannedMealId = mealId,
            RecipeId = null,
            ProductId = productId,
            Servings = servings,
            Ordinal = ordinal,
        };
    }

    internal void SetServings(int servings)
    {
        if (servings < 1) throw new ArgumentOutOfRangeException(nameof(servings), "Servings must be >= 1 (M3).");
        Servings = servings;
    }

    /// <summary>
    /// Repositions this dish within its meal. Used by <see cref="PlannedMeal.UpdateDishes"/> to
    /// move a kept (identity-preserved) dish to its new position without recreating it.
    /// </summary>
    internal void SetOrdinal(int ordinal) => Ordinal = ordinal;
}
