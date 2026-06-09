using Plantry.Catalog.Domain;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;

namespace Plantry.Web.Inventory;

/// <summary>
/// The Web-side adapter for the conversion seam (the confirmed Port + Web-adapter decision). It is
/// the one place Inventory's consumption need meets Catalog's pure <see cref="UnitConverter"/>: it
/// loads the product's conversion overrides and the household's units, then hands
/// <see cref="ProductStock.Consume"/> a converter bound to that product. Lives in Plantry.Web — the
/// composition root that already references both contexts — so neither Inventory project depends on Catalog.
/// </summary>
public sealed class CatalogConversionProvider(IProductRepository products, IUnitRepository units)
    : IProductConversionProvider
{
    // Scoped per-request: cache units to avoid N list queries when building the pantry list.
    private IReadOnlyCollection<Unit>? _cachedUnits;

    public async Task<IQuantityConverter> ForProductAsync(Guid productId, CancellationToken ct = default)
    {
        _cachedUnits ??= await units.ListAsync(ct);
        var product = await products.FindAsync(ProductId.From(productId), ct);
        IReadOnlyCollection<ProductConversion> conversions = product?.Conversions ?? [];
        return new CatalogQuantityConverter(_cachedUnits, conversions);
    }

    public async Task<IReadOnlyDictionary<Guid, IQuantityConverter>> ForProductsAsync(
        IEnumerable<Guid> productIds, CancellationToken ct = default)
    {
        _cachedUnits ??= await units.ListAsync(ct);
        var idList = productIds.Select(ProductId.From).ToList();
        var productList = await products.ListWithConversionsAsync(idList, ct);
        var conversionsByProductId = productList.ToDictionary(p => p.Id.Value, p => p.Conversions);
        return idList.ToDictionary(
            id => id.Value,
            id => (IQuantityConverter)new CatalogQuantityConverter(
                _cachedUnits,
                conversionsByProductId.TryGetValue(id.Value, out var c) ? c : []));
    }

    private sealed class CatalogQuantityConverter(
        IReadOnlyCollection<Unit> units,
        IReadOnlyCollection<ProductConversion> conversions) : IQuantityConverter
    {
        public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId) =>
            UnitConverter.Convert(amount, fromUnitId, toUnitId, units, conversions);
    }
}
