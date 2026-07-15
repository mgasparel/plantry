namespace Plantry.Catalog.Domain;

public interface IProductRepository
{
    Task<Product?> FindAsync(ProductId id, CancellationToken ct = default);
    Task<Product?> FindByNameAsync(string name, CancellationToken ct = default);

    /// <summary>Active (non-archived) products, for the default catalog list view.</summary>
    Task<List<Product>> ListActiveAsync(CancellationToken ct = default);

    /// <summary>Active (non-archived) products with their SKUs eager-loaded — for the intake
    /// review form, which needs pack-size options per matched product.</summary>
    Task<List<Product>> ListActiveWithSkusAsync(CancellationToken ct = default);

    /// <summary>Loads the specified products with their conversion rules in a single query — for batch paths that need converters.</summary>
    Task<List<Product>> ListWithConversionsAsync(IEnumerable<ProductId> ids, CancellationToken ct = default);

    /// <summary>
    /// Every variant of <paramref name="parentId"/> — <b>including archived ones</b>, with their
    /// conversions loaded. Cross-aggregate parent/variant consistency (the denormalized
    /// <see cref="Product.HasVariants"/> flag and one-time inheritance) must see archived variants
    /// too: an archived variant still carries <c>ParentProductId</c> and can be unarchived later.
    /// </summary>
    Task<List<Product>> ListVariantsAsync(ProductId parentId, CancellationToken ct = default);

    /// <summary>
    /// Loads the given products PLUS every variant child of any parent among them, in a single query —
    /// for batch fulfillment resolution that needs the DM-19 parent/variant tree without an N+1
    /// per-parent round-trip (plantry-jnhs). Archived products and variants are included (callers
    /// filter the tree as they need). Ids absent from the household are omitted.
    ///
    /// <para>The default implementation falls back to a per-id <see cref="FindAsync"/> +
    /// <see cref="ListVariantsAsync"/> loop so test doubles need not reimplement it; the EF repository
    /// overrides it with one query.</para>
    /// </summary>
    async Task<List<Product>> ListWithVariantsAsync(IReadOnlyList<ProductId> ids, CancellationToken ct = default)
    {
        var result = new List<Product>();
        foreach (var id in ids)
        {
            var product = await FindAsync(id, ct);
            if (product is null) continue;
            result.Add(product);
            if (product.IsParent)
                result.AddRange(await ListVariantsAsync(id, ct));
        }
        return result;
    }

    Task AddAsync(Product product, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
