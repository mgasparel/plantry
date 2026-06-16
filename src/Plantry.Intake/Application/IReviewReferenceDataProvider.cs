namespace Plantry.Intake.Application;

/// <summary>
/// Port: loads the household's Catalog reference data — products, units, locations, categories — for the
/// review-form dropdowns. Implemented in Plantry.Web (adapter over Catalog's read services) so
/// Plantry.Intake stays free of any Catalog dependency, mirroring <see cref="ICatalogHintProvider"/> and
/// <see cref="ICreateProductPort"/>. Only stock-eligible products are returned: a receipt line can only
/// ever resolve to a product that can hold stock.
/// </summary>
public interface IReviewReferenceDataProvider
{
    Task<ReviewReferenceData> GetAsync(CancellationToken ct = default);
}

/// <summary>A purchasable pack-size option for a matched product, shown in the intake review drawer.</summary>
public sealed record ReviewSkuOption(Guid Id, string Label);

public sealed record ReviewProductOption(
    Guid Id,
    string Name,
    string DefaultUnitCode,
    /// <summary>The product's default stocking unit — required: every Product carries a non-null
    /// DefaultUnitId on the aggregate, so this is a required parameter, never defaulted.</summary>
    Guid DefaultUnitId,
    Guid? DefaultLocationId,
    IReadOnlyList<ReviewSkuOption> Skus,
    /// <summary>Default shelf-life in days from the date of purchase — null when the product has no expiry default.</summary>
    int? DefaultDueDays = null,
    /// <summary>Category id of this product. Null when uncategorised.</summary>
    Guid? CategoryId = null,
    /// <summary>Hue in degrees (0–359) from the product's category. Null when uncategorised or no hue assigned.</summary>
    int? CategoryHue = null);

public sealed record ReviewUnitOption(Guid Id, string Code, string Name);

public sealed record ReviewLocationOption(Guid Id, string Name);

public sealed record ReviewCategoryOption(
    Guid Id,
    string Name,
    /// <summary>Hue in degrees (0–359) on the oklch colour wheel. Null means no hue assigned (renders neutral chip).</summary>
    int? Hue = null);

public sealed record ReviewReferenceData(
    IReadOnlyList<ReviewProductOption> Products,
    IReadOnlyList<ReviewUnitOption> Units,
    IReadOnlyList<ReviewLocationOption> Locations,
    IReadOnlyList<ReviewCategoryOption> Categories);
