using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.Shopping.Application;

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

        var allUnits = await units.ListAsync(ct);
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
        var allUnits = await units.ListAsync(ct);
        var product = await products.FindAsync(ProductId.From(productId), ct);
        IReadOnlyCollection<ProductConversion> conversions = product?.Conversions ?? [];

        var result = UnitConverter.Convert(amount, fromUnitId, toUnitId, allUnits, conversions);
        return result.IsSuccess ? result.Value : null;
    }

    public async Task<IReadOnlyList<ShoppingUnitOption>> ListUnitsAsync(CancellationToken ct = default)
    {
        var allUnits = await units.ListAsync(ct);
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
