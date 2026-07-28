using Plantry.Catalog.Application;
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
    ILocationRepository locations,
    IHouseholdExpiryDefaultsReader expiryDefaults) : ICatalogReadFacade
{
    public async Task<CatalogProductInfo?> FindProductAsync(Guid productId, CancellationToken ct = default)
    {
        var product = await products.FindAsync(ProductId.From(productId), ct);
        if (product is null) return null;

        var unitsById = (await units.ListAsync(ct)).ToDictionary(u => u.Id.Value);
        (string? name, int? hue) categoryInfo = product.CategoryId is { } categoryId
            ? await categories.FindAsync(categoryId, ct) is { } cat ? (cat.Name, cat.Hue) : (null, null)
            : (null, null);
        var defaults = await expiryDefaults.GetDefaultsAsync(ct);

        return ToInfo(product, unitsById, categoryInfo.name, categoryInfo.hue, defaults);
    }

    public async Task<IReadOnlyList<CatalogProductInfo>> ListProductsAsync(CancellationToken ct = default) =>
        await ProjectAsync(await products.ListActiveAsync(ct), ct);

    public async Task<IReadOnlyList<CatalogProductInfo>> ListArchivedProductsAsync(CancellationToken ct = default) =>
        await ProjectAsync(await products.ListArchivedAsync(ct), ct);

    private async Task<IReadOnlyList<CatalogProductInfo>> ProjectAsync(List<Product> source, CancellationToken ct)
    {
        var unitsById = (await units.ListAsync(ct)).ToDictionary(u => u.Id.Value);
        var categoriesById = (await categories.ListAsync(ct)).ToDictionary(c => c.Id);
        // Resolved once per call (household reference data, not per-product) — mirrors the single
        // unitsById/categoriesById batch above rather than an N+1 per product.
        var defaults = await expiryDefaults.GetDefaultsAsync(ct);

        return source
            .Select(p =>
            {
                var (catName, catHue) = p.CategoryId is { } cid && categoriesById.TryGetValue(cid, out var cat)
                    ? (cat.Name, cat.Hue)
                    : ((string?)null, (int?)null);
                return ToInfo(p, unitsById, catName, catHue, defaults);
            })
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetUnitCodesAsync(CancellationToken ct = default) =>
        (await units.ListAsync(ct)).ToDictionary(u => u.Id.Value, u => u.Code);

    public async Task<IReadOnlyDictionary<Guid, string>> GetLocationNamesAsync(CancellationToken ct = default) =>
        (await locations.ListAsync(ct)).ToDictionary(l => l.Id.Value, l => l.Name);

    public async Task<IReadOnlyDictionary<Guid, bool>> GetLocationFrozenFlagsAsync(CancellationToken ct = default) =>
        (await locations.ListAsync(ct)).ToDictionary(l => l.Id.Value, l => l.IsFrozen);

    private static CatalogProductInfo ToInfo(
        Product p, Dictionary<Guid, Unit> unitsById, string? categoryName, int? categoryHue,
        (int AfterFreezing, int AfterThawing) householdDefaults) =>
        new(
            p.Id.Value,
            p.Name,
            categoryName,
            p.DefaultUnitId.Value,
            unitsById.TryGetValue(p.DefaultUnitId.Value, out var unit) ? unit.Code : "?",
            p.CanHoldStock,
            p.IsVariant,
            CategoryHue: categoryHue,
            DefaultDueDaysAfterOpening: ExpiryDefaultResolver.ResolveDefaultDueDaysAfterOpening(p),
            // Household-default fallback (plantry-hh1f): a product with no per-product override (e.g.
            // an auto-created leftovers product, plantry-hh1f's original report) now resolves to the
            // household's freeze/thaw defaults instead of leaving TransferStockCommand with nothing to
            // recompute from.
            DefaultDueDaysAfterFreezing: ExpiryDefaultResolver.ResolveDefaultDueDaysAfterFreezing(p, householdDefaults.AfterFreezing),
            DefaultDueDaysAfterThawing: ExpiryDefaultResolver.ResolveDefaultDueDaysAfterThawing(p, householdDefaults.AfterThawing),
            IsArchived: p.IsArchived);
}
