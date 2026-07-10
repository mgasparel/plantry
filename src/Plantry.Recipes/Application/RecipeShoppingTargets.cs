using Plantry.Recipes.Domain;

namespace Plantry.Recipes.Application;

/// <summary>
/// Computes the target <see cref="ShoppingItem"/> sets that the recipe Detail buttons sync to the
/// shopping list (plantry-gsj). Shared by the write-path application services
/// (<see cref="AddMissingToShoppingList"/> / <see cref="AddIngredientsToShoppingList"/>) and the read-path
/// button-label computation (the Web Detail page model), so the set a button SETS and the set its label
/// is computed over can never drift.
///
/// <para>Both sets are computed over a recipe's <b>expanded</b> view (recipe-composition.md §7, D4): the
/// caller passes the aggregated <see cref="EffectiveIngredient"/> set (from
/// <see cref="ExpandedLineAggregation.AggregateByProductAndUnit"/>), so included recipes' products are on the
/// list and duplicate subs (D14) are already merged into one row per <c>(ProductId, UnitId)</c>. A flat
/// recipe aggregates to its own ingredient set, so its targets are unchanged.</para>
/// </summary>
public static class RecipeShoppingTargets
{
    /// <summary>
    /// "Add missing" target set: one line per Missing/Low effective ingredient with a positive shortfall
    /// (scaledRequired − available), in the ingredient's unit. Delegates to the expanded
    /// <see cref="RecipeShortfallCalculator.Compute(IReadOnlyList{EffectiveIngredient},ExpandedFulfillmentResult,int,int)"/>
    /// so the shortfall rule is single-sourced.
    /// </summary>
    public static IReadOnlyList<ShoppingItem> Missing(
        IReadOnlyList<EffectiveIngredient> lines,
        ExpandedFulfillmentResult fulfillment,
        int defaultServings,
        int desiredServings) =>
        RecipeShortfallCalculator.Compute(lines, fulfillment, defaultServings, desiredServings)
            .Select(s => new ShoppingItem(s.ProductId, s.ShortfallQuantity, s.UnitId))
            .ToList();

    /// <summary>
    /// "Add all" target set: every quantity-bearing (Quantity+UnitId) effective ingredient whose product is
    /// stock-tracked, scaled to <paramref name="desiredServings"/>. Untracked staples and products not in
    /// <paramref name="trackedProductIds"/> are excluded (C12, plantry-yukq) — mirroring the definition used
    /// by the fulfilment "tracked" label and the Cook flow.
    /// </summary>
    public static IReadOnlyList<ShoppingItem> All(
        IReadOnlyList<EffectiveIngredient> lines,
        IReadOnlySet<Guid> trackedProductIds,
        int defaultServings,
        int desiredServings)
    {
        var scale = (decimal)desiredServings / defaultServings;

        return lines
            .Where(l => l.Quantity.HasValue && l.UnitId.HasValue && trackedProductIds.Contains(l.ProductId))
            .Select(l => new ShoppingItem(
                ProductId: l.ProductId,
                Quantity: Math.Round(l.Quantity!.Value * scale, 3),
                UnitId: l.UnitId!.Value))
            .ToList();
    }
}
