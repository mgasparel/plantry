using Plantry.Catalog.Domain;
using Plantry.Deals.Domain;
using Plantry.Shopping.Application;

namespace Plantry.Web.Shopping;

/// <summary>
/// Web-layer adapter implementing <see cref="IShoppingDealAttributionReader"/> — resolves the store behind
/// a Deal <b>contribution</b> (the <c>deal_id</c> that placed the item on the list) so the shopping board
/// can render the "on sale at {store}" attribution label (plantry-jwyb).
///
/// <para>
/// Distinct from <c>ShoppingDealReaderAdapter</c> (the product-keyed cheapest-active-deal <i>badge</i>, which
/// reads Pricing): a Deal contribution's <c>SourceRef</c> is a <c>deal_id</c>, and the store behind that
/// specific deal is a Deals-owned fact — read here via Deals' <see cref="IDealRepository"/> for the deal's
/// <see cref="Deal.StoreId"/>, then resolved to a display name over Catalog's <see cref="IStoreRepository"/>
/// (the <c>store_id</c> is a soft-ref to <c>catalog.store</c>, DM-16). Grounded in ADR-010's context map
/// (<c>DEAL → SHOP</c>; "Deal badges are a read-time join with Deals/Pricing"). Lives in Plantry.Web (the
/// composition root referencing both Deals and Catalog) so the Shopping projects stay <c>→ SharedKernel only</c>.
/// Household scoping is enforced at the Postgres RLS level (ADR-008) on both the Deals and Catalog connections.
/// </para>
/// </summary>
public sealed class ShoppingDealAttributionReaderAdapter(IDealRepository deals, IStoreRepository stores)
    : IShoppingDealAttributionReader
{
    public async Task<IReadOnlyDictionary<Guid, string>> GetDealStoreNamesAsync(
        IReadOnlyList<Guid> dealIds,
        CancellationToken ct = default)
    {
        if (dealIds.Count == 0)
            return new Dictionary<Guid, string>();

        // Resolve each distinct deal to its store id. A deal not found in the household (deleted/foreign)
        // is skipped, so the caller falls back to a plain "on sale" label.
        var dealToStore = new Dictionary<Guid, Guid>();
        foreach (var dealId in dealIds.Distinct())
        {
            var deal = await deals.FindAsync(DealId.From(dealId), ct);
            if (deal is not null)
                dealToStore[dealId] = deal.StoreId;
        }

        if (dealToStore.Count == 0)
            return new Dictionary<Guid, string>();

        // Batch-resolve merchant display names over Catalog (incl. archived, so an unsubscribed store still
        // resolves a name — mirrors ShoppingDealReaderAdapter).
        var storeIds = dealToStore.Values.ToHashSet();
        var allStores = await stores.ListAsync(ct);
        var storeNames = allStores
            .Where(s => storeIds.Contains(s.Id.Value))
            .ToDictionary(s => s.Id.Value, s => s.Name);

        var result = new Dictionary<Guid, string>();
        foreach (var (dealId, storeId) in dealToStore)
        {
            // Omit deals whose store cannot be resolved so the caller falls back to plain "on sale".
            if (storeNames.TryGetValue(storeId, out var name))
                result[dealId] = name;
        }

        return result;
    }
}
