using Plantry.Catalog.Domain;
using Plantry.Recipes.Application;
using Plantry.SharedKernel;

namespace Plantry.Web.Recipes;

/// <summary>
/// Web-side adapter for <see cref="ICatalogProductReader"/> — supplies the Recipes context with the
/// product facts it needs (name, <c>track_stock</c>, default unit, depth-1 parent/variant tree) and the
/// unit codes for rendering quantities, over Catalog's <see cref="IProductRepository"/> and
/// <see cref="IUnitRepository"/>. Lives in Plantry.Web, the composition root that already references
/// both contexts, so the Recipes projects stay free of any Catalog dependency.
/// </summary>
public sealed class CatalogProductReaderAdapter(
    IProductRepository products,
    IUnitRepository units,
    ICategoryRepository categories)
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

        var active = await products.ListActiveAsync(ct);

        // Build a name→product map for lookup after ranking (names are unique within a household catalog).
        var byName = active.ToDictionary(p => p.Name);

        // Rank via the shared ProductNameMatcher (same algorithm as TakeStockReaderAdapter,
        // so results are consistent wherever the _ProductSearchCreateSheet is used).
        var hits = ProductNameMatcher.Rank(
            active.Select(p => (p.Id.Value, p.Name)),
            nameQuery.Trim());

        return hits
            .Select(h =>
            {
                var p = byName[h.Name];
                return new CatalogProductCandidate(p.Id.Value, p.Name, p.TrackStock, p.DefaultUnitId.Value, h.Score);
            })
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

    public async Task<IReadOnlyList<CatalogUnitOption>> ListUnitsAsync(CancellationToken ct = default)
    {
        var all = await units.ListAsync(ct);
        return all
            .OrderBy(u => u.Code)
            .Select(u => new CatalogUnitOption(u.Id.Value, u.Code, u.Dimension.ToDbValue(), u.FactorToBase))
            .ToList();
    }

    public async Task<IReadOnlyList<CatalogGroupOption>> ListGroupsAsync(CancellationToken ct = default)
    {
        var active = await products.ListActiveAsync(ct);
        return active
            .Where(p => p.IsParent)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new CatalogGroupOption(p.Id.Value, p.Name))
            .ToList();
    }

    public async Task<IReadOnlyList<CatalogCategoryOption>> ListCategoriesAsync(CancellationToken ct = default)
    {
        var activeCategories = await categories.ListActiveAsync(ct);
        return activeCategories
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new CatalogCategoryOption(c.Id.Value, c.Name))
            .ToList();
    }

    private static readonly IReadOnlyDictionary<Guid, CatalogProductSummary> EmptySummaries =
        new Dictionary<Guid, CatalogProductSummary>();
    private static readonly IReadOnlyDictionary<Guid, string> EmptyCodes = new Dictionary<Guid, string>();
}
