using Plantry.Catalog.Domain;
using Plantry.Pricing.Application;
using Plantry.Shopping.Application;

namespace Plantry.Web.Shopping;

/// <summary>
/// Web-side adapter for <see cref="IShoppingDealReader"/> — supplies the Shopping list read model with the
/// cheapest active deal per product (P5-9) by delegating to <b>Pricing</b>'s
/// <see cref="PricingQueries.CheapestActiveDealAsync"/> read model (ADR-010: the "active deal per product"
/// read model is owned by Pricing; Shopping depends on Pricing, <b>never Deals</b>). The merchant name is
/// resolved over Catalog's own <see cref="IStoreRepository"/> (the <c>store_id</c> is a soft-ref to
/// <c>catalog.store</c>, DM-16) — Shopping never reads Deals' <c>ICatalogStoreReader</c> nor any Deals table.
///
/// <para>Lives in Plantry.Web, the composition root that already references both Pricing and Catalog, so the
/// Shopping projects stay <c>→ SharedKernel only</c>. Household scoping is enforced at the Postgres RLS level
/// (ADR-008) by the <c>HouseholdRlsConnectionInterceptor</c> on both the Pricing and Catalog connections, so
/// no additional household filter is needed here.</para>
///
/// <para>The read is per-product, mirroring Pricing's per-product read surface (the same shape as the recipe
/// cost badge's <c>IPriceReader.FindLatestAsync</c>); store names are then resolved in a single batch to
/// avoid an N+1 on the merchant lookup.</para>
/// </summary>
public sealed class ShoppingDealReaderAdapter(PricingQueries pricing, IStoreRepository stores)
    : IShoppingDealReader
{
    public async Task<IReadOnlyDictionary<Guid, ShoppingActiveDeal>> GetActiveDealsAsync(
        IReadOnlyList<Guid> productIds,
        DateOnly today,
        CancellationToken ct = default)
    {
        if (productIds.Count == 0)
            return new Dictionary<Guid, ShoppingActiveDeal>();

        // Resolve the cheapest active deal per distinct product from Pricing's read model.
        var found = new Dictionary<Guid, (Guid DealId, Guid? StoreId)>();
        foreach (var productId in productIds.Distinct())
        {
            var observation = await pricing.CheapestActiveDealAsync(productId, today, ct);
            if (observation is not null)
                found[productId] = (observation.SourceRef, observation.StoreId);
        }

        if (found.Count == 0)
            return new Dictionary<Guid, ShoppingActiveDeal>();

        // Batch-resolve merchant display names over Catalog (incl. archived, so an unsubscribed store still
        // renders a name). Deals with no store_id skip the lookup and fall back to the storeless badge.
        var storeIds = found.Values
            .Where(v => v.StoreId.HasValue)
            .Select(v => v.StoreId!.Value)
            .Distinct()
            .ToHashSet();

        IReadOnlyDictionary<Guid, string> storeNames = new Dictionary<Guid, string>();
        if (storeIds.Count > 0)
        {
            var allStores = await stores.ListAsync(ct);
            storeNames = allStores
                .Where(s => storeIds.Contains(s.Id.Value))
                .ToDictionary(s => s.Id.Value, s => s.Name);
        }

        return found.ToDictionary(
            kv => kv.Key,
            kv => new ShoppingActiveDeal(
                ProductId: kv.Key,
                DealId: kv.Value.DealId,
                StoreId: kv.Value.StoreId,
                StoreName: kv.Value.StoreId is { } sid && storeNames.TryGetValue(sid, out var name)
                    ? name
                    : null));
    }
}
