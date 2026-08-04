using Plantry.Pantry.Domain;

namespace Plantry.Pantry.Application;

/// <summary>
/// Adapter for <see cref="ICatalogReadFacade"/> — supplies the Inventory-side read models and the
/// intake guard with Catalog facts (product existence/stock-eligibility and reference-data names) over
/// the Catalog repositories directly. An intra-context Pantry collaboration now that Catalog and
/// Inventory live in one assembly (ADR-024, plantry-g3da.6).
/// </summary>
public sealed class CatalogReadFacade(
    IProductRepository products,
    UnitCodesAccessor unitCodes,
    ICategoryRepository categories,
    ILocationRepository locations,
    IHouseholdExpiryDefaultsReader expiryDefaults) : ICatalogReadFacade
{
    public async Task<CatalogProductInfo?> FindProductAsync(Guid productId, CancellationToken ct = default)
    {
        var product = await products.FindAsync(ProductId.From(productId), ct);
        if (product is null) return null;

        // Never-expiry decisions are live through a variant's parent, unlike the snapshot-inherited
        // day-count fields. The list projection batches these parent facts below.
        var parent = product.ParentProductId is { } parentId
            ? await products.FindAsync(parentId, ct)
            : null;

        var unitCodesById = await unitCodes.GetCodesAsync(ct);
        (string? name, int? hue) categoryInfo = product.CategoryId is { } categoryId
            ? await categories.FindAsync(categoryId, ct) is { } cat ? (cat.Name, cat.Hue) : (null, null)
            : (null, null);
        var defaults = await expiryDefaults.GetDefaultsAsync(ct);

        return ToInfo(product, parent, unitCodesById, categoryInfo.name, categoryInfo.hue, defaults);
    }

    public async Task<IReadOnlyList<CatalogProductInfo>> ListProductsAsync(CancellationToken ct = default) =>
        await ProjectAsync(await products.ListActiveAsync(ct), ct);

    public async Task<IReadOnlyList<CatalogProductInfo>> ListArchivedProductsAsync(CancellationToken ct = default) =>
        await ProjectAsync(await products.ListArchivedAsync(ct), ct);

    private async Task<IReadOnlyList<CatalogProductInfo>> ProjectAsync(List<Product> source, CancellationToken ct)
    {
        var unitCodesById = await unitCodes.GetCodesAsync(ct);
        var categoriesById = (await categories.ListAsync(ct)).ToDictionary(c => c.Id);
        // Resolved once per call (household reference data, not per-product) — mirrors the single
        // unitCodesById/categoriesById batch above rather than an N+1 per product.
        var defaults = await expiryDefaults.GetDefaultsAsync(ct);
        var parentIds = source
            .Select(p => p.ParentProductId)
            .OfType<ProductId>()
            .Distinct()
            .ToList();
        var parents = parentIds.Count == 0
            ? []
            : await products.ListByIdsAsync(parentIds, ct);
        var parentsById = parents.ToDictionary(p => p.Id);

        return source
            .Select(p =>
            {
                var (catName, catHue) = p.CategoryId is { } cid && categoriesById.TryGetValue(cid, out var cat)
                    ? (cat.Name, cat.Hue)
                    : ((string?)null, (int?)null);
                var parent = p.ParentProductId is { } parentId && parentsById.TryGetValue(parentId, out var parentProduct)
                    ? parentProduct
                    : null;
                return ToInfo(p, parent, unitCodesById, catName, catHue, defaults);
            })
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetUnitCodesAsync(CancellationToken ct = default) =>
        await unitCodes.GetCodesAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, string>> GetLocationNamesAsync(CancellationToken ct = default) =>
        (await locations.ListAsync(ct)).ToDictionary(l => l.Id.Value, l => l.Name);

    public async Task<IReadOnlyDictionary<Guid, bool>> GetLocationFrozenFlagsAsync(CancellationToken ct = default) =>
        (await locations.ListAsync(ct)).ToDictionary(l => l.Id.Value, l => l.IsFrozen);

    private static CatalogProductInfo ToInfo(
        Product p, Product? parent, IReadOnlyDictionary<Guid, string> unitCodesById, string? categoryName, int? categoryHue,
        (int AfterFreezing, int AfterThawing) householdDefaults) =>
        new(
            p.Id.Value,
            p.Name,
            categoryName,
            p.DefaultUnitId.Value,
            unitCodesById.TryGetValue(p.DefaultUnitId.Value, out var code) ? code : "?",
            p.CanHoldStock,
            p.IsVariant,
            CategoryHue: categoryHue,
            DefaultDueDaysAfterOpening: ExpiryDefaultResolver.ResolveDefaultDueDaysAfterOpening(p),
            AfterFreezingPolicy: ExpiryDefaultResolver.ResolveAfterFreezing(p, parent, householdDefaults.AfterFreezing),
            AfterThawingPolicy: ExpiryDefaultResolver.ResolveAfterThawing(p, parent, householdDefaults.AfterThawing),
            IsArchived: p.IsArchived);
}
