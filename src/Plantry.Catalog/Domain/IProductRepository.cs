namespace Plantry.Catalog.Domain;

public interface IProductRepository
{
    Task<Product?> FindAsync(ProductId id, CancellationToken ct = default);
    Task<Product?> FindByNameAsync(string name, CancellationToken ct = default);

    /// <summary>Active (non-archived) products, for the default catalog list view.</summary>
    Task<List<Product>> ListActiveAsync(CancellationToken ct = default);

    /// <summary>Loads the specified products with their conversion rules in a single query — for batch paths that need converters.</summary>
    Task<List<Product>> ListWithConversionsAsync(IEnumerable<ProductId> ids, CancellationToken ct = default);

    /// <summary>
    /// Every variant of <paramref name="parentId"/> — <b>including archived ones</b>, with their
    /// conversions loaded. Cross-aggregate parent/variant consistency (the denormalized
    /// <see cref="Product.HasVariants"/> flag and one-time inheritance) must see archived variants
    /// too: an archived variant still carries <c>ParentProductId</c> and can be unarchived later.
    /// </summary>
    Task<List<Product>> ListVariantsAsync(ProductId parentId, CancellationToken ct = default);

    Task AddAsync(Product product, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
