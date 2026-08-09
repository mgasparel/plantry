using Plantry.Pantry.Application;
using Plantry.Pantry.Domain;
using Plantry.Planning.Application;

namespace Plantry.Web.Shopping;

/// <summary>
/// Web-layer adapter implementing <see cref="IShoppingCatalogReader"/> over the Catalog
/// bounded context's repositories. This is the anti-corruption layer seam between Shopping
/// and Catalog — Shopping never takes a direct dependency on Catalog's EF context or repositories.
/// Follows the same adapter pattern as <c>CatalogProductReaderAdapter</c> (Recipes → Catalog ACL).
/// </summary>
public sealed class ShoppingCatalogReaderAdapter(
    IProductRepository products,
    ICategoryRepository categories,
    IUnitRepository units)
    : IShoppingCatalogReader
{
    // Per-request memoization (plantry-e016): this adapter is registered AddScoped, so caching across calls
    // within one request is safe. ShoppingBasketCostingService.EstimateAsync calls TryConvertAsync once per
    // unchecked line item — without this, every htmx mutation handler's RefreshListAsync (check-off, uncheck,
    // edit-qty, add, delete, recategorize) would re-issue a full units table scan and a per-product lookup for
    // every uncertain-unit line, on the app's most-touched flow.
    private IReadOnlyList<Unit>? _units;
    private readonly Dictionary<Guid, Product?> _productCache = [];

    private async Task<IReadOnlyList<Unit>> GetUnitsAsync(CancellationToken ct) =>
        _units ??= await units.ListAsync(ct);

    private async Task<Product?> GetProductAsync(Guid productId, CancellationToken ct)
    {
        if (_productCache.TryGetValue(productId, out var cached))
            return cached;

        var product = await products.FindAsync(ProductId.From(productId), ct);
        _productCache[productId] = product;
        return product;
    }

    public async Task<IReadOnlyDictionary<Guid, ShoppingProductSummary>> ResolveSummariesAsync(
        IReadOnlyList<Guid> productIds,
        CancellationToken ct = default)
    {
        if (productIds.Count == 0)
            return new Dictionary<Guid, ShoppingProductSummary>();

        var allProducts = await products.ListActiveAsync(ct);
        var categoriesById = (await categories.ListAsync(ct)).ToDictionary(c => c.Id);

        return productIds
            .Join(allProducts, id => id, p => p.Id.Value, (id, p) => (id, product: p))
            .ToDictionary(
                t => t.id,
                t =>
                {
                    Category? category = t.product.CategoryId is { } categoryId
                        && categoriesById.TryGetValue(categoryId, out var cat)
                        ? cat
                        : null;

                    return new ShoppingProductSummary(
                        t.id,
                        t.product.Name,
                        CategoryName: category?.Name,
                        CategoryHue: category?.Hue);
                });
    }

    public async Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
        IReadOnlyList<Guid> unitIds,
        CancellationToken ct = default)
    {
        if (unitIds.Count == 0)
            return new Dictionary<Guid, string>();

        var allUnits = await GetUnitsAsync(ct);
        return allUnits
            .Where(u => unitIds.Contains(u.Id.Value))
            .ToDictionary(u => u.Id.Value, u => u.Code);
    }

    public async Task<IReadOnlyList<ShoppingProductCandidate>> ListProductsAsync(
        CancellationToken ct = default)
    {
        var allProducts = await products.ListActiveAsync(ct);
        return allProducts
            .Where(p => p.CanHoldStock)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new ShoppingProductCandidate(p.Id.Value, p.Name))
            .ToList();
    }

    public async Task<decimal?> TryConvertAsync(
        decimal amount,
        Guid fromUnitId,
        Guid toUnitId,
        Guid productId,
        CancellationToken ct = default)
    {
        var allUnits = await GetUnitsAsync(ct);
        var product = await GetProductAsync(productId, ct);
        IReadOnlyCollection<ProductConversion> conversions = product?.Conversions ?? [];

        var result = UnitConverter.Convert(amount, fromUnitId, toUnitId, allUnits, conversions);
        return result.IsSuccess ? result.Value : null;
    }

    public async Task<IReadOnlyList<ShoppingUnitOption>> ListUnitsAsync(CancellationToken ct = default)
    {
        var allUnits = await GetUnitsAsync(ct);
        return UnitQueries.OrderForDropdown(allUnits)
            .Select(u => new ShoppingUnitOption(u.Id.Value, u.Code, u.Name, u.Dimension.ToDbValue()))
            .ToList();
    }

    public async Task<IReadOnlyList<ShoppingCategoryOption>> ListCategoriesAsync(CancellationToken ct = default)
    {
        var activeCategories = await categories.ListActiveAsync(ct);
        return activeCategories
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ShoppingCategoryOption(c.Id.Value, c.Name, c.Hue))
            .ToList();
    }
}
