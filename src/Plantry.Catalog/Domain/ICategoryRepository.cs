namespace Plantry.Catalog.Domain;

public interface ICategoryRepository
{
    Task<Category?> FindAsync(CategoryId id, CancellationToken ct = default);
    Task<Category?> FindByNameAsync(string name, CancellationToken ct = default);

    /// <summary>All categories incl. archived — for resolving the names of products' (FK-less) category references.</summary>
    Task<List<Category>> ListAsync(CancellationToken ct = default);

    /// <summary>Active (non-archived) categories — for the management list and product-edit dropdowns.</summary>
    Task<List<Category>> ListActiveAsync(CancellationToken ct = default);

    Task AddAsync(Category category, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
