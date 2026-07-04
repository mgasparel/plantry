namespace Plantry.Shopping.Application;

/// <summary>
/// Anti-corruption read port: Shopping's read model needs pantry on-hand quantities and low-stock
/// flags from the Inventory context. This interface is defined in Shopping.Application and
/// implemented in the Web layer (an adapter over <c>IProductStockRepository</c> and Inventory's
/// catalog read facade), following the same ACL pattern as <see cref="IShoppingCatalogReader"/>
/// (ADR-002 — Shopping must NOT read Inventory's EF context directly).
///
/// <para>The port exposes only what Shopping's two consumers need: on-hand quantity in the
/// product's display unit, the display unit code, and a pre-computed <c>IsLow</c> flag. Shopping
/// never receives raw lot data, journal rows, or any other Inventory aggregate internals.</para>
/// </summary>
public interface IShoppingPantryReader
{
    /// <summary>
    /// Returns on-hand stock level for a set of product ids in one batch call.
    /// Products with no stock record (never stocked) are omitted from the result dictionary.
    /// Products whose household record exists but has no active lots are included with
    /// <see cref="ShoppingPantryStockLevel.OnHand"/> set to zero and
    /// <see cref="ShoppingPantryStockLevel.IsLow"/> set to <c>false</c> — an out product is not
    /// "running low" (see the flag's own doc). Out is inferred by the caller from <c>OnHand ≤ 0</c>.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ShoppingPantryStockLevel>> GetStockLevelsAsync(
        IReadOnlyList<Guid> productIds,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all household pantry products that are restock candidates: running low
    /// (<see cref="ShoppingPantryStockLevel.IsLow"/> is <c>true</c>, i.e. 0 &lt; onHand ≤ threshold)
    /// OR out (<c>OnHand ≤ 0</c>). Out products are included even though their <c>IsLow</c> is
    /// <c>false</c> — a depleted staple is as much a restock candidate as a low one. Used by the
    /// "Running low in your pantry" suggestions strip (plantry-48l) to discover which products to
    /// surface regardless of whether they are already on the current shopping list. The caller is
    /// responsible for excluding products already present on the list and for applying the display cap.
    /// </summary>
    Task<IReadOnlyList<ShoppingPantryStockLevel>> GetLowStockProductsAsync(
        CancellationToken ct = default);
}

/// <summary>
/// On-hand stock summary for one product — the Shopping context's view of the pantry.
/// Quantities are already aggregated into the product's display unit by the adapter.
/// </summary>
/// <param name="ProductId">The product this level covers.</param>
/// <param name="OnHand">
/// Total on-hand quantity, in <see cref="UnitCode"/> units, summed across all active lots.
/// Zero when the product is tracked but has no active stock.
/// </param>
/// <param name="UnitCode">
/// The display unit code (e.g. "g", "ml", "ea") in which <see cref="OnHand"/> is expressed.
/// Matches the product's default unit from the Catalog context.
/// </param>
/// <param name="IsLow">
/// True when the product is <em>running low</em> — a positive but low quantity,
/// 0 &lt; <see cref="OnHand"/> ≤ the household's low stock threshold (see
/// <c>ProductStock.IsRunningLow</c>). Deliberately <c>false</c> when out
/// (<see cref="OnHand"/> ≤ 0) so that out and low are distinct, mutually-exclusive states —
/// Shopping renders low as the "· low" warning sub-line and out as "out", never both together.
/// A product with no threshold set is never running low, so this flag is <c>false</c> for it
/// regardless of quantity.
/// </param>
public sealed record ShoppingPantryStockLevel(
    Guid ProductId,
    decimal OnHand,
    string UnitCode,
    bool IsLow);
