namespace Plantry.Recipes.Application;

/// <summary>
/// Anti-corruption read port onto Inventory (recipes-domain-model.md §8). Gives the Recipes context
/// the live stock snapshot it needs for fulfillment computation — available quantity (in the product's
/// default unit plus FEFO active-lot facts) — without coupling Recipes to Inventory's domain model. Defined
/// here in Recipes.Application and <b>implemented in Plantry.Web</b> over
/// <c>InventoryQueryService</c> / <c>ICatalogReadFacade</c>, so the Recipes project keeps its
/// <c>→ SharedKernel only</c> dependency. All identifiers cross as raw <see cref="Guid"/> soft refs
/// (DM-3). For parent products (DM-19), the adapter must return a <see cref="ProductStock"/> entry
/// per <b>variant child</b> so <c>FulfillmentService</c> can roll up total stock across them.
/// </summary>
public interface IInventoryStockReader
{
    /// <summary>
    /// Returns the available quantity (summed active lots, already in the product's default unit),
    /// FEFO-ordered active-lot facts, and the soonest expiry date across all active lots for the given
    /// product. Returns null when the product has no <c>product_stock</c> record (i.e. has never been
    /// stocked).
    /// </summary>
    Task<ProductStock?> FindStockAsync(Guid productId, CancellationToken ct = default);

    /// <summary>
    /// Returns stock snapshots for all listed product ids in a single round-trip — for the common case
    /// of computing fulfillment for an entire recipe. Products with no stock record are omitted from the
    /// result dictionary. Ids absent from the household are likewise omitted.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ProductStock>> FindStockBatchAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default);
}

/// <summary>
/// A point-in-time stock snapshot for one product, in that product's default unit. Used by
/// <c>FulfillmentService</c> for availability comparison and expiry-soon flagging.
/// </summary>
/// <param name="ProductId">The product this snapshot covers.</param>
/// <param name="AvailableQuantity">
/// Total available quantity, aggregated into the product's default unit from all active lots via
/// the existing <c>InventoryQueryService</c> display-quantity logic. Zero when the product is
/// tracked but has no active stock.
/// </param>
/// <param name="DefaultUnitId">The unit in which <see cref="AvailableQuantity"/> is expressed.</param>
/// <param name="SoonestExpiry">
/// Earliest expiry date across all active lots; null when no lots carry an expiry date.
/// </param>
/// <param name="ActiveLots">
/// FEFO-ordered active lots. Each quantity remains in the lot's own unit so the Recipes rule engine can
/// allocate only the quantity it would consume, including when an earlier lot is insufficient and a later
/// lot must be reached. Null is accepted for backwards-compatible test doubles; callers synthesize the
/// aggregate snapshot as one lot when this list is absent.
/// </param>
public sealed record ProductStock(
    Guid ProductId,
    decimal AvailableQuantity,
    Guid DefaultUnitId,
    DateOnly? SoonestExpiry,
    IReadOnlyList<ActiveStockLot>? ActiveLots = null);

/// <summary>
/// The quantity, unit, and expiry facts for one active Inventory lot. The list supplied by
/// <see cref="IInventoryStockReader"/> is already in Inventory's deterministic FEFO order.
/// </summary>
/// <param name="AvailableQuantity">Remaining positive quantity in <paramref name="UnitId"/>.</param>
/// <param name="UnitId">The Catalog unit in which this lot's quantity is stored.</param>
/// <param name="ExpiryDate">The lot's materialized expiry, or null when it has no expiry.</param>
public sealed record ActiveStockLot(
    decimal AvailableQuantity,
    Guid UnitId,
    DateOnly? ExpiryDate);
