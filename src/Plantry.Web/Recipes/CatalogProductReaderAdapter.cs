using Plantry.Catalog.Domain;
using Plantry.Recipes.Application;

namespace Plantry.Web.Recipes;

/// <summary>
/// Web-side adapter for <see cref="ICatalogProductReader"/> — supplies the Recipes context with the
/// product facts it needs (name, <c>track_stock</c>, default unit, depth-1 parent/variant tree) over
/// Catalog's <see cref="IProductRepository"/>. Lives in Plantry.Web, the composition root that already
/// references both contexts, so the Recipes projects stay free of any Catalog dependency.
/// </summary>
public sealed class CatalogProductReaderAdapter(IProductRepository products) : ICatalogProductReader
{
    public async Task<CatalogProduct?> FindAsync(Guid productId, CancellationToken ct = default)
    {
        var product = await products.FindAsync(ProductId.From(productId), ct);
        if (product is null) return null;

        // Parent/variant tree (DM-10/DM-19): when this product is a parent, expose its live variants
        // for fulfillment rollup; ListVariantsAsync returns archived variants too, so filter them out.
        IReadOnlyList<Guid> variantIds = product.IsParent
            ? (await products.ListVariantsAsync(product.Id, ct))
                .Where(v => !v.IsArchived)
                .Select(v => v.Id.Value)
                .ToList()
            : [];

        return new CatalogProduct(
            product.Id.Value,
            product.Name,
            product.TrackStock,
            product.DefaultUnitId.Value,
            product.ParentProductId?.Value,
            product.IsParent,
            variantIds);
    }

    public async Task<IReadOnlyList<CatalogProductCandidate>> SearchAsync(string nameQuery, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nameQuery)) return [];

        var query = nameQuery.Trim();
        var active = await products.ListActiveAsync(ct);
        return active
            .Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(p => new CatalogProductCandidate(p.Id.Value, p.Name, p.TrackStock, p.DefaultUnitId.Value))
            .ToList();
    }
}
