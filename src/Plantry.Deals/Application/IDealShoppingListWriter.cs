using Plantry.Deals.Domain;

namespace Plantry.Deals.Application;

/// <summary>
/// Anti-corruption write port onto the Shopping context for the Deals context (DM-18, the reused P2-4 seam).
/// A stock-up alert's "Add to list" action places the deal's product on the household shopping list stamped
/// <c>source="deal"</c> and <c>source_ref=deal_id</c>; Shopping applies its own merge rule so an
/// already-listed item is topped up rather than duplicated (DM-18).
///
/// <para>Deals owns this port rather than reusing <c>Recipes.Application.IShoppingListWriter</c> — a
/// cross-context reference Deals cannot take (DM-3). It mirrors MealPlanning's
/// <c>IMealPlanShoppingWriter</c>: a per-context port on top, the same Shopping <c>AddItemCommand</c>
/// underneath. Implemented in <c>Plantry.Web</c> (the composition root that references both contexts).</para>
/// </summary>
public interface IDealShoppingListWriter
{
    /// <summary>
    /// Adds the deal's <paramref name="productId"/> to the household shopping list with
    /// <c>source="deal"</c> and <c>source_ref=<paramref name="dealId"/></c>. Idempotent per
    /// (source, source_ref): re-adding the same deal for the same product merges into the existing
    /// contribution rather than inserting a duplicate row (Shopping's merge rule, DM-18).
    /// </summary>
    Task AddItemAsync(Guid productId, DealId dealId, CancellationToken ct = default);
}
