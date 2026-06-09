using Plantry.Catalog.Domain;
using Plantry.Inventory.Application;

namespace Plantry.Web.Inventory;

/// <summary>
/// Web-side adapter for <see cref="ICatalogReadFacade"/> — supplies the Inventory read models and the
/// intake guard with Catalog facts (product existence/stock-eligibility and reference-data names) over
/// the Catalog repositories. Keeps the Inventory projects free of any Catalog dependency.
/// </summary>
public sealed class CatalogReadFacade(
    IProductRepository products,
    IUnitRepository units,
    ICategoryRepository categories,
    ILocationRepository locations) : ICatalogReadFacade
{
    public async Task<CatalogProductInfo?> FindProductAsync(Guid productId, CancellationToken ct = default)
    {
        var product = await products.FindAsync(ProductId.From(productId), ct);
        if (product is null) return null;

        var unitsById = (await units.ListAsync(ct)).ToDictionary(u => u.Id.Value);
        string? categoryName = product.CategoryId is { } categoryId
            ? (await categories.FindAsync(categoryId, ct))?.Name
            : null;

        return ToInfo(product, unitsById, categoryName);
    }

    public async Task<IReadOnlyList<CatalogProductInfo>> ListProductsAsync(CancellationToken ct = default)
    {
        var active = await products.ListActiveAsync(ct);
        var unitsById = (await units.ListAsync(ct)).ToDictionary(u => u.Id.Value);
        var categoriesById = (await categories.ListAsync(ct)).ToDictionary(c => c.Id);

        return active
            .Select(p => ToInfo(p, unitsById,
                p.CategoryId is { } categoryId && categoriesById.TryGetValue(categoryId, out var c) ? c.Name : null))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetUnitCodesAsync(CancellationToken ct = default) =>
        (await units.ListAsync(ct)).ToDictionary(u => u.Id.Value, u => u.Code);

    public async Task<IReadOnlyDictionary<Guid, string>> GetLocationNamesAsync(CancellationToken ct = default) =>
        (await locations.ListAsync(ct)).ToDictionary(l => l.Id.Value, l => l.Name);

    private static CatalogProductInfo ToInfo(Product p, Dictionary<Guid, Unit> unitsById, string? categoryName) =>
        new(
            p.Id.Value,
            p.Name,
            categoryName,
            p.DefaultUnitId.Value,
            unitsById.TryGetValue(p.DefaultUnitId.Value, out var unit) ? unit.Code : "?",
            p.CanHoldStock,
            p.IsVariant);
}
