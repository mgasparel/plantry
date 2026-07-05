using Plantry.Catalog.Domain;
using Plantry.Deals.Application;

namespace Plantry.Web.Deals;

/// <summary>
/// Web-side adapter for <see cref="ICatalogStoreReader"/> — resolves <c>catalog.store</c> identities for
/// the Deals §7e list over Catalog's own <see cref="IStoreRepository"/>. Lives in Plantry.Web (the
/// composition root) so Deals never takes a direct dependency on Catalog's EF context (ADR-010/DM-3),
/// mirroring <c>MealPlanCatalogProductReaderAdapter</c>.
/// </summary>
public sealed class CatalogStoreReaderAdapter(IStoreRepository stores) : ICatalogStoreReader
{
    public async Task<CatalogStoreInfo?> FindAsync(Guid storeId, CancellationToken ct = default)
    {
        var store = await stores.FindAsync(StoreId.From(storeId), ct);
        return store is null ? null : new CatalogStoreInfo(store.Id.Value, store.Name, store.ExternalRef);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(
        IReadOnlyList<Guid> storeIds, CancellationToken ct = default)
    {
        if (storeIds.Count == 0) return new Dictionary<Guid, string>();

        var wanted = storeIds.ToHashSet();
        // ListAsync includes archived stores, so an unsubscribed/archived merchant still resolves a name.
        var all = await stores.ListAsync(ct);
        return all
            .Where(s => wanted.Contains(s.Id.Value))
            .ToDictionary(s => s.Id.Value, s => s.Name);
    }
}
