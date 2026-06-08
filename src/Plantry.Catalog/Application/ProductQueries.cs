using Plantry.Catalog.Domain;

namespace Plantry.Catalog.Application;

public sealed record ProductListItem(
    ProductId Id,
    string Name,
    string? CategoryName,
    string DefaultUnitCode,
    bool IsArchived,
    bool IsVariant,
    bool IsParent);

public sealed record ProductSkuDetail(
    ProductSkuId Id,
    string Label,
    decimal? SizeQuantity,
    string? SizeUnitCode);

public sealed record ProductVariantSummary(ProductId Id, string Name);

public sealed record ProductConversionDetail(
    ProductConversionId Id,
    string FromUnitCode,
    string ToUnitCode,
    decimal Factor);

public sealed record ProductDetail(
    ProductId Id,
    string Name,
    string? CategoryName,
    string DefaultUnitCode,
    string? DefaultLocationName,
    int? DefaultDueDays,
    int? DefaultDueDaysAfterOpening,
    int? DefaultDueDaysAfterFreezing,
    int? DefaultDueDaysAfterThawing,
    bool IsArchived,
    string? ParentName,
    IReadOnlyList<ProductVariantSummary> Variants,
    IReadOnlyList<ProductSkuDetail> Skus,
    IReadOnlyList<ProductConversionDetail> Conversions,
    decimal? LatestPrice);

/// <summary>
/// Joins <see cref="Product"/> aggregates against their reference-data names for the list/detail
/// pages. <see cref="ProductDetail.LatestPrice"/> is left null — Slice 5 wires up Pricing.
/// </summary>
public sealed class ProductQueryService(
    IProductRepository products,
    IUnitRepository units,
    ICategoryRepository categories,
    ILocationRepository locations)
{
    public async Task<IReadOnlyList<ProductListItem>> ListActiveAsync(CancellationToken ct = default)
    {
        var activeProducts = await products.ListActiveAsync(ct);
        var unitsById = (await units.ListAsync(ct)).ToDictionary(u => u.Id);
        var categoriesById = (await categories.ListAsync(ct)).ToDictionary(c => c.Id);

        return activeProducts
            .Select(p => new ProductListItem(
                p.Id,
                p.Name,
                p.CategoryId is { } categoryId && categoriesById.TryGetValue(categoryId, out var category) ? category.Name : null,
                unitsById.TryGetValue(p.DefaultUnitId, out var unit) ? unit.Code : "?",
                p.IsArchived,
                p.IsVariant,
                p.IsParent))
            .ToList();
    }

    public async Task<ProductDetail?> FindDetailAsync(ProductId id, CancellationToken ct = default)
    {
        var product = await products.FindAsync(id, ct);
        if (product is null) return null;

        var unitsById = (await units.ListAsync(ct)).ToDictionary(u => u.Id);
        var activeProducts = await products.ListActiveAsync(ct);

        string? categoryName = null;
        if (product.CategoryId is { } categoryId)
            categoryName = (await categories.FindAsync(categoryId, ct))?.Name;

        string? locationName = null;
        if (product.DefaultLocationId is { } locationId)
            locationName = (await locations.FindAsync(locationId, ct))?.Name;

        string? parentName = null;
        if (product.ParentProductId is { } parentId)
            parentName = (await products.FindAsync(parentId, ct))?.Name;

        var variants = activeProducts
            .Where(p => p.ParentProductId == product.Id)
            .Select(p => new ProductVariantSummary(p.Id, p.Name))
            .ToList();

        var skus = product.Skus
            .Select(sku => new ProductSkuDetail(
                sku.Id,
                sku.Label,
                sku.SizeQuantity,
                sku.SizeUnitId is { } sizeUnitId && unitsById.TryGetValue(sizeUnitId, out var sizeUnit) ? sizeUnit.Code : null))
            .ToList();

        var conversions = product.Conversions
            .Select(conversion => new ProductConversionDetail(
                conversion.Id,
                unitsById.TryGetValue(conversion.FromUnitId, out var fromUnit) ? fromUnit.Code : "?",
                unitsById.TryGetValue(conversion.ToUnitId, out var toUnit) ? toUnit.Code : "?",
                conversion.Factor))
            .ToList();

        return new ProductDetail(
            product.Id,
            product.Name,
            categoryName,
            unitsById.TryGetValue(product.DefaultUnitId, out var defaultUnit) ? defaultUnit.Code : "?",
            locationName,
            product.DefaultDueDays,
            product.DefaultDueDaysAfterOpening,
            product.DefaultDueDaysAfterFreezing,
            product.DefaultDueDaysAfterThawing,
            product.IsArchived,
            parentName,
            variants,
            skus,
            conversions,
            LatestPrice: null);
    }
}
