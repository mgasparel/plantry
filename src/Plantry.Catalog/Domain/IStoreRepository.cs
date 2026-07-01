namespace Plantry.Catalog.Domain;

/// <summary>
/// Read/write port for the household's stores (catalog.md DM-16). Mirrors
/// <see cref="ILocationRepository"/> — reads and writes on one port, per house convention — and
/// is the "IStoreReader over the household's stores" the Phase 5 plan calls for. All queries are
/// RLS-scoped to the current household by <c>CatalogDbContext</c>.
/// </summary>
public interface IStoreRepository
{
    Task<Store?> FindAsync(StoreId id, CancellationToken ct = default);
    Task<Store?> FindByNameAsync(string name, CancellationToken ct = default);

    /// <summary>Resolves a merchant by its external directory id — the key <c>EnsureStore</c> subscribes on.</summary>
    Task<Store?> FindByExternalRefAsync(string externalRef, CancellationToken ct = default);

    /// <summary>All stores incl. archived — for resolving the names of (FK-less) store references.</summary>
    Task<List<Store>> ListAsync(CancellationToken ct = default);

    /// <summary>Active (non-archived) stores — for the §7e management list.</summary>
    Task<List<Store>> ListActiveAsync(CancellationToken ct = default);

    Task AddAsync(Store store, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
