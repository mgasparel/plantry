namespace Plantry.Recipes.Application;

/// <summary>
/// One product-level line of a recipe's <b>expanded</b> view after duplicate-merge (recipe-composition.md
/// §7, D14). Produced by aggregating a <see cref="RecipeExpansionService"/> <see cref="ExpandedLine"/> list
/// by <c>(ProductId, UnitId)</c> so a sub-recipe included more than once (D14) — or a product that appears
/// both directly and inside a sub — collapses into a single row the downstream consumers (shortfall,
/// shopping list, costing, fulfillment) treat exactly like a flat ingredient.
///
/// <para>Quantity carries the product of the inclusion factors already baked in by expansion (source qty ×
/// factors along the path); it is <b>not</b> yet scaled to a desired serving count — each consumer applies
/// its own <c>desiredServings / defaultServings</c> scale on top, mirroring how the flat consumers scale
/// <c>recipe.Ingredients</c>. Untracked staples (null quantity/unit) pass through as a single row per
/// product with null quantity/unit (C12 applies downstream as today).</para>
/// </summary>
/// <param name="ProductId">Soft ref → catalog.product (DM-3).</param>
/// <param name="Quantity">
/// Summed expanded quantity across every path that resolved to this <c>(ProductId, UnitId)</c>, or null for
/// an untracked staple. Not yet scaled to the desired serving count.
/// </param>
/// <param name="UnitId">Soft ref → catalog.unit (DM-3); null for an untracked staple.</param>
public sealed record EffectiveIngredient(Guid ProductId, decimal? Quantity, Guid? UnitId);

/// <summary>
/// Aggregation of a flat <see cref="ExpandedLine"/> list into the <see cref="EffectiveIngredient"/> set the
/// expanded shortfall / shopping / costing / fulfillment consumers read (recipe-composition.md §7, D4/D14).
/// </summary>
public static class ExpandedLineAggregation
{
    /// <summary>
    /// Collapses <paramref name="lines"/> to one <see cref="EffectiveIngredient"/> per <c>(ProductId, UnitId)</c>
    /// so duplicate subs (D14) merge and nothing double-counts. Tracked lines (quantity <b>and</b> unit present)
    /// are summed per <c>(ProductId, UnitId)</c>; untracked staples (null quantity/unit) pass through as one
    /// row per distinct product with null quantity/unit (C12). A flat recipe with distinct products aggregates
    /// to exactly its own ingredient set (aggregation is a no-op), so flat behaviour is unchanged.
    /// </summary>
    public static IReadOnlyList<EffectiveIngredient> AggregateByProductAndUnit(this IReadOnlyList<ExpandedLine> lines)
    {
        var result = new List<EffectiveIngredient>();

        // Tracked lines: merge by (ProductId, UnitId), summing the expanded quantities (D14).
        var tracked = lines
            .Where(l => l.Quantity.HasValue && l.UnitId.HasValue)
            .GroupBy(l => (l.ProductId, UnitId: l.UnitId!.Value))
            .Select(g => new EffectiveIngredient(g.Key.ProductId, g.Sum(l => l.Quantity!.Value), g.Key.UnitId));
        result.AddRange(tracked);

        // Untracked staples (null quantity or unit): one pass-through row per distinct product (C12).
        var passthrough = lines
            .Where(l => !(l.Quantity.HasValue && l.UnitId.HasValue))
            .Select(l => l.ProductId)
            .Distinct()
            .Select(pid => new EffectiveIngredient(pid, null, null));
        result.AddRange(passthrough);

        return result;
    }
}
