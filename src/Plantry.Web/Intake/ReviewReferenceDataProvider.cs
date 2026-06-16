using Plantry.Catalog.Domain;
using Plantry.Intake.Application;

namespace Plantry.Web.Intake;

/// <summary>
/// Web-side adapter for <see cref="IReviewReferenceDataProvider"/> — projects the household's active
/// Catalog reference data (stock-eligible products, units, locations, categories) into the review-form
/// dropdown options. Reads Catalog repositories directly so Plantry.Intake stays free of any Catalog
/// dependency, mirroring <see cref="CatalogHintProvider"/>. Parent products are excluded: a receipt line
/// can never resolve to one (it cannot hold stock).
/// </summary>
public sealed class ReviewReferenceDataProvider(
    IProductRepository products,
    IUnitRepository units,
    ILocationRepository locations,
    ICategoryRepository categories) : IReviewReferenceDataProvider
{
    public async Task<ReviewReferenceData> GetAsync(CancellationToken ct = default)
    {
        var activeProducts = await products.ListActiveWithSkusAsync(ct);
        var activeUnits = await units.ListAsync(ct);
        var activeLocations = await locations.ListActiveAsync(ct);
        var activeCategories = await categories.ListActiveAsync(ct);

        var unitCodesById = activeUnits.ToDictionary(u => u.Id, u => u.Code);

        var categoriesById = activeCategories.ToDictionary(c => c.Id);

        var productOptions = activeProducts
            .Where(p => p.CanHoldStock)
            .Select(p =>
            {
                var categoryHue = p.CategoryId is { } cid && categoriesById.TryGetValue(cid, out var cat)
                    ? cat.Hue
                    : null;
                return new ReviewProductOption(
                    p.Id.Value,
                    p.Name,
                    unitCodesById.TryGetValue(p.DefaultUnitId, out var code) ? code : "?",
                    p.DefaultUnitId.Value,
                    p.DefaultLocationId?.Value,
                    p.Skus.Select(s => new ReviewSkuOption(s.Id.Value, s.Label)).ToList(),
                    DefaultDueDays: p.DefaultDueDays,
                    CategoryId: p.CategoryId?.Value,
                    CategoryHue: categoryHue);
            })
            .ToList();

        var unitOptions = activeUnits
            .Select(u => new ReviewUnitOption(u.Id.Value, u.Code, u.Name))
            .ToList();

        var locationOptions = activeLocations
            .Select(l => new ReviewLocationOption(l.Id.Value, l.Name))
            .ToList();

        var categoryOptions = activeCategories
            .Select(c => new ReviewCategoryOption(c.Id.Value, c.Name, c.Hue))
            .ToList();

        return new ReviewReferenceData(productOptions, unitOptions, locationOptions, categoryOptions);
    }
}
