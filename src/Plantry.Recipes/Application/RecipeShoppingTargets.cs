using Plantry.Recipes.Domain;

namespace Plantry.Recipes.Application;

/// <summary>
/// Computes the target <see cref="ShoppingItem"/> sets that the recipe Detail buttons sync to the
/// shopping list (plantry-gsj). Shared by the write-path application services
/// (<see cref="AddMissingToShoppingList"/> / <see cref="AddIngredientsToShoppingList"/>) and the read-path
/// button-label computation (the Web Detail page model), so the set a button SETS and the set its label
/// is computed over can never drift.
/// </summary>
public static class RecipeShoppingTargets
{
    /// <summary>
    /// "Add missing" target set: one line per Missing/Low ingredient with a positive shortfall
    /// (scaledRequired − available), in the ingredient's unit. Delegates to
    /// <see cref="RecipeShortfallCalculator"/> so the shortfall rule is single-sourced.
    /// </summary>
    public static IReadOnlyList<ShoppingItem> Missing(
        Recipe recipe,
        FulfillmentResult fulfillment,
        int desiredServings) =>
        RecipeShortfallCalculator.Compute(recipe, fulfillment, desiredServings)
            .Select(s => new ShoppingItem(s.ProductId, s.ShortfallQuantity, s.UnitId))
            .ToList();

    /// <summary>
    /// "Add all" target set: every quantity-bearing (Quantity+UnitId) ingredient whose product is
    /// stock-tracked, scaled to <paramref name="desiredServings"/>. Untracked staples and products not
    /// in <paramref name="trackedProductIds"/> are excluded (C12, plantry-yukq) — mirroring the
    /// definition used by the fulfilment "tracked" label and the Cook flow.
    /// </summary>
    public static IReadOnlyList<ShoppingItem> All(
        Recipe recipe,
        IReadOnlySet<Guid> trackedProductIds,
        int desiredServings)
    {
        var scale = (decimal)desiredServings / recipe.DefaultServings;

        return recipe.Ingredients
            .Where(i => i.Quantity.HasValue && i.UnitId.HasValue && trackedProductIds.Contains(i.ProductId))
            .Select(i => new ShoppingItem(
                ProductId: i.ProductId,
                Quantity: Math.Round(i.Quantity!.Value * scale, 3),
                UnitId: i.UnitId!.Value))
            .ToList();
    }
}
