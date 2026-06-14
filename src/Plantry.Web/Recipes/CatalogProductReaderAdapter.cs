using Plantry.Catalog.Domain;
using Plantry.Recipes.Application;

namespace Plantry.Web.Recipes;

/// <summary>
/// Web-side adapter for <see cref="ICatalogProductReader"/> — supplies the Recipes context with the
/// product facts it needs (name, <c>track_stock</c>, default unit, depth-1 parent/variant tree) and the
/// unit codes for rendering quantities, over Catalog's <see cref="IProductRepository"/> and
/// <see cref="IUnitRepository"/>. Lives in Plantry.Web, the composition root that already references
/// both contexts, so the Recipes projects stay free of any Catalog dependency.
/// </summary>
public sealed class CatalogProductReaderAdapter(IProductRepository products, IUnitRepository units)
    : ICatalogProductReader
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

    public async Task<IReadOnlyDictionary<Guid, CatalogProductSummary>> ResolveSummariesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        if (productIds.Count == 0) return EmptySummaries;

        // Single batched query (no per-row N+1); the variant tree is intentionally not loaded here.
        var found = await products.ListWithConversionsAsync(productIds.Distinct().Select(ProductId.From), ct);
        return found.ToDictionary(
            p => p.Id.Value,
            p => new CatalogProductSummary(p.Id.Value, p.Name, p.TrackStock));
    }

    public async Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
        IReadOnlyList<Guid> unitIds, CancellationToken ct = default)
    {
        if (unitIds.Count == 0) return EmptyCodes;

        // Units are small household reference data; one ListAsync, filtered to the requested ids
        // (mirrors CatalogReadFacade.GetUnitCodesAsync).
        var wanted = unitIds.ToHashSet();
        var all = await units.ListAsync(ct);
        return all
            .Where(u => wanted.Contains(u.Id.Value))
            .ToDictionary(u => u.Id.Value, u => u.Code);
    }

    private static readonly IReadOnlyDictionary<Guid, CatalogProductSummary> EmptySummaries =
        new Dictionary<Guid, CatalogProductSummary>();
    private static readonly IReadOnlyDictionary<Guid, string> EmptyCodes = new Dictionary<Guid, string>();
}
