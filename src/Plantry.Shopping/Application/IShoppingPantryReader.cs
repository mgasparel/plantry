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
    /// <see cref="ShoppingPantryStockLevel.OnHand"/> set to zero and <see cref="ShoppingPantryStockLevel.IsLow"/>
    /// set to <c>true</c>.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ShoppingPantryStockLevel>> GetStockLevelsAsync(
        IReadOnlyList<Guid> productIds,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all household pantry products with <see cref="ShoppingPantryStockLevel.IsLow"/> set to
    /// <c>true</c> (at or below par, or with no active stock). Used by the "Running low in your pantry"
    /// suggestions strip (plantry-48l) to discover which products to surface regardless of whether they
    /// are already on the current shopping list. The caller is responsible for excluding products already
    /// present on the list and for applying the display cap.
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
/// True when on-hand stock is at or below par (or when the product has no active stock at all).
/// Shopping renders this as the "· low" warning sub-line and highlights low items in the
/// search dropdown. When a par level is not yet defined for the product, this flag is true
/// if and only if <see cref="OnHand"/> is zero (out of stock).
/// </param>
public sealed record ShoppingPantryStockLevel(
    Guid ProductId,
    decimal OnHand,
    string UnitCode,
    bool IsLow);
