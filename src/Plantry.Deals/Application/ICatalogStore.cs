namespace Plantry.Deals.Application;

/// <summary>A resolved <c>catalog.store</c> identity, projected for Deals by ID only (DM-3).</summary>
public sealed record CatalogStoreInfo(Guid StoreId, string Name, string? ExternalRef);

/// <summary>
/// Read port onto Catalog's store reference data (DM-16) — resolves a subscribed store's display
/// identity for the §7e list. Deals holds only the <c>store_id</c> soft-ref and never reads
/// <c>CatalogDbContext</c> directly (ADR-010/DM-3); the Web adapter implements this over Catalog's
/// <c>IStoreRepository</c>.
/// </summary>
public interface ICatalogStoreReader
{
    /// <summary>Resolves a single store's identity, or null if the id is unknown.</summary>
    Task<CatalogStoreInfo?> FindAsync(Guid storeId, CancellationToken ct = default);

    /// <summary>
    /// Resolves display names for the given store ids (incl. archived, so an unsubscribed merchant still
    /// renders a name). Ids with no matching store are omitted from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(
        IReadOnlyList<Guid> storeIds, CancellationToken ct = default);
}

/// <summary>
/// Write port onto Catalog's store reference data — the ensure-by-external-identity a subscribe performs
/// before creating a <c>StoreSubscription</c> (DJ1 step 3). The Web adapter wraps Catalog's own
/// <c>EnsureStoreCommand</c>: idempotent, reusing/adopting/reactivating the existing row (P5-1) so exactly
/// one <c>catalog.store</c> exists per merchant.
/// </summary>
public interface ICatalogStoreWriter
{
    /// <summary>Ensures a <c>catalog.store</c> exists for the merchant and returns its id.</summary>
    Task<Guid> EnsureAsync(string externalRef, string name, CancellationToken ct = default);
}
