namespace Plantry.Shopping.Application;

/// <summary>
/// Anti-corruption read port: Shopping's read model needs the store a <b>deal contribution</b> came
/// from to resolve Deal-source contribution labels ("on sale at Metro") on the shopping board's
/// attribution sub-line (plantry-jwyb).
///
/// <para>
/// This is a <b>separate port</b> from <see cref="IShoppingDealReader"/>. That one powers the
/// product-keyed cheapest-active-deal <i>badge</i> (P5-9) and is keyed off <c>product_id</c> against
/// Pricing's read model; it does not resolve a Deal <i>contribution</i>'s <c>SourceRef</c>. A Deal
/// contribution's <c>SourceRef</c> is the <c>deal_id</c> that placed the item on the list
/// (DealShoppingListWriterAdapter — <c>source=Deal, source_ref=dealId</c>), and the store behind that
/// specific deal is a Deals-owned fact with no <c>deal_id</c>-keyed equivalent in Pricing's read model.
/// </para>
///
/// <para>
/// Defined here in Shopping.Application (which keeps its <c>→ SharedKernel only</c> dependency) and
/// implemented in Plantry.Web over Deals' <c>IDealRepository</c> (for the deal's <c>StoreId</c>) plus
/// Catalog's <c>IStoreRepository</c> (for the merchant name) — grounded in ADR-010's context map
/// (<c>DEAL → SHOP</c>; "Deal badges are a read-time join with Deals/Pricing"). All identifiers cross
/// as raw <see cref="System.Guid"/> soft-refs (DM-3). Household scoping is enforced at the Postgres RLS
/// level (ADR-008) on the Deals and Catalog connections.
/// </para>
/// </summary>
public interface IShoppingDealAttributionReader
{
    /// <summary>
    /// Resolves the store display name for a set of deal ids in one batch call. Deal ids not found in
    /// the household (deleted or foreign), or whose store cannot be resolved, are silently omitted from
    /// the result dictionary so the caller can fall back to a plain "on sale" label.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetDealStoreNamesAsync(
        IReadOnlyList<Guid> dealIds,
        CancellationToken ct = default);
}
