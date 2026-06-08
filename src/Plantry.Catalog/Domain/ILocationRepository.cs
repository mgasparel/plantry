namespace Plantry.Catalog.Domain;

public interface ILocationRepository
{
    Task<Location?> FindAsync(LocationId id, CancellationToken ct = default);
    Task<Location?> FindByNameAsync(string name, CancellationToken ct = default);

    /// <summary>All locations incl. archived — for resolving the names of products' (FK-less) location references.</summary>
    Task<List<Location>> ListAsync(CancellationToken ct = default);

    /// <summary>Active (non-archived) locations — for the management list and product-edit dropdowns.</summary>
    Task<List<Location>> ListActiveAsync(CancellationToken ct = default);

    Task AddAsync(Location location, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
