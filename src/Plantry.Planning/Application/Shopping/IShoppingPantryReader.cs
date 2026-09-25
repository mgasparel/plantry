namespace Plantry.Planning.Application;

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
///
/// <para><b>Parent fold (plantry-oh27.3 — "intent lives at the parent; facts live at the leaf",
/// ARCHITECTURE.md §Product Groups):</b> a variant is represented in the restock-candidate ports
/// (<see cref="GetLowStockProductsAsync"/>, <see cref="GetFrequentStapleProductsAsync"/>) only through
/// its OWN <c>LowStockRule</c>; without one, its PARENT stands in for it — Tier 1 (running low/out) when
/// the parent has a rule, Tier 2 (frequent staple) via the UNION of its live variants' purchase dates
/// when the parent has none. A parent's on-hand is Σ its live (non-archived) variants converted to the
/// parent's default unit. A leaf with no parent is unaffected. <see cref="GetStockLevelsAsync"/> also
/// resolves a directly-requested parent id the same way, since a shopping-list item may target a parent
/// directly.</para>
/// </summary>
public interface IShoppingPantryReader
{
    /// <summary>
    /// Returns on-hand stock level for a set of product ids in one batch call. A requested id may be a
    /// leaf or a parent (plantry-oh27.3) — a parent's level is its live variants' on-hand rolled up via
    /// the shared <c>IOnHandRollupQuery</c> (plantry-oh27.2). Leaf products with no stock record (never
    /// stocked) are omitted from the result dictionary. Leaf products whose household record exists but
    /// has no active lots are included with <see cref="ShoppingPantryStockLevel.OnHand"/> set to zero and
    /// <see cref="ShoppingPantryStockLevel.IsLow"/> set to <c>false</c> — an out product is not
    /// "running low" (see the flag's own doc). Out is inferred by the caller from <c>OnHand ≤ 0</c>.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ShoppingPantryStockLevel>> GetStockLevelsAsync(
        IReadOnlyList<Guid> productIds,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all household pantry restock candidates: running low
    /// (<see cref="ShoppingPantryStockLevel.IsLow"/> is <c>true</c>, i.e. 0 &lt; onHand ≤ threshold)
    /// OR out (<c>OnHand ≤ 0</c>). Out products are included even though their <c>IsLow</c> is
    /// <c>false</c> — a depleted staple is as much a restock candidate as a low one. Each result row is
    /// either a leaf with no parent (unchanged), a variant with its OWN threshold rule, or a PARENT
    /// standing in for its ruleless variants (plantry-oh27.3, see the port's own summary) — a parent
    /// appears at most once and its OnHand is the rolled-up sum, never a raw variant total.
    ///
    /// <para><b>Excludes produced products</b> (Catalog's <c>Product.IsProduced</c> — a recipe yield or
    /// cook leftover, "made at home, not bought", plantry-sn6v) even when they read as low or out: a
    /// produced product is not a restock candidate by definition, so it is never a "buy this" suggestion.
    /// The exclusion lives here, in the adapter, rather than in the caller — every consumer of this
    /// port inherits the fix for free.</para>
    ///
    /// Used by the "Running low in your pantry" suggestions strip (plantry-48l) to discover which
    /// products to surface regardless of whether they are already on the current shopping list. The
    /// caller is responsible for excluding products already present on the list and for applying the
    /// display cap.
    /// </summary>
    Task<IReadOnlyList<ShoppingPantryStockLevel>> GetLowStockProductsAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Returns products matching the shared Tidy Up D4 frequent-staple predicate and no threshold. A
    /// variant with no own rule is folded into its parent's group (union of purchase dates across every
    /// live variant); the group is a candidate only when the group's own product (parent or leaf) has no
    /// threshold rule (plantry-oh27.3).
    /// </summary>
    Task<IReadOnlyList<ShoppingPantryStockLevel>> GetFrequentStapleProductsAsync(
        DateOnly today, CancellationToken ct = default);
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
/// <param name="IsParent">
/// True when <see cref="ProductId"/> is a parent product standing in for its ruleless variants
/// (plantry-oh27.3) rather than a leaf. Purely informational today — no tier/dedup logic branches on
/// it — kept so a future UI affordance ("any variant") can render differently without another port
/// round trip.
/// </param>
public sealed record ShoppingPantryStockLevel(
    Guid ProductId,
    decimal OnHand,
    string UnitCode,
    bool IsLow,
    bool HasLowStockThreshold = true,
    bool IsParent = false);
