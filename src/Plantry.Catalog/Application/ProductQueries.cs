using Plantry.Catalog.Domain;
using Plantry.SharedKernel.Domain;

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
    decimal Factor,
    ConversionSource Source)
{
    /// <summary>True while this conversion is an unendorsed machine guess (ADR-022) — drives the UI tag + Promote action.</summary>
    public bool IsAiSuggested => Source == ConversionSource.AiSuggested;
}

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
    bool IsParent,
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
    ILocationRepository locations,
    IClock clock)
{
    public async Task<IReadOnlyList<ProductListItem>> ListActiveAsync(CancellationToken ct = default) =>
        await ProjectAsync(await products.ListActiveAsync(ct), ct);

    /// <summary>
    /// Every product, active and archived (plantry-lxm2) — feeds the Pantry "Everything" scope so
    /// archived products stay reachable inline (a neutral "Archived" badge, still clickable through
    /// to the product detail page) instead of disappearing from every list the moment they're
    /// archived — the only route back to the Unarchive control. <see cref="ProductListItem.IsArchived"/>
    /// distinguishes the two on the resulting rows.
    /// </summary>
    public async Task<IReadOnlyList<ProductListItem>> ListEverythingAsync(CancellationToken ct = default)
    {
        var activeProducts = await products.ListActiveAsync(ct);
        var archivedProducts = await products.ListArchivedAsync(ct);
        return await ProjectAsync([.. activeProducts, .. archivedProducts], ct);
    }

    private async Task<IReadOnlyList<ProductListItem>> ProjectAsync(IEnumerable<Product> source, CancellationToken ct)
    {
        var unitsById = (await units.ListAsync(ct)).ToDictionary(u => u.Id);
        var categoriesById = (await categories.ListAsync(ct)).ToDictionary(c => c.Id);

        return source
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
                conversion.Factor,
                conversion.Source))
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
            product.IsParent,
            parentName,
            variants,
            skus,
            conversions,
            LatestPrice: null);
    }

    /// <summary>
    /// DM-11 default-expiry composition, shared by every Add Stock sheet (Pantry Index, Product Detail):
    /// product-level default wins, else the product's category default, else no default at all
    /// (<see cref="ExpiryDefaultResolver.ResolveDefaultDueDays"/>), materialized as today + dueDays. An
    /// explicitly entered date always wins over this — that guard stays at the call site (form semantics,
    /// not policy) rather than here.
    /// </summary>
    public async Task<DateOnly?> DefaultExpiryDateAsync(Guid productId, CancellationToken ct = default)
    {
        var product = await products.FindAsync(ProductId.From(productId), ct);
        if (product is null) return null;

        Category? category = product.CategoryId is { } categoryId ? await categories.FindAsync(categoryId, ct) : null;
        return ExpiryDefaultResolver.ResolveDefaultDueDays(product, category) is { } dueDays
            ? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddDays(dueDays)
            : null;
    }
}
