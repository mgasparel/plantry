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

public sealed record ReviewProductOption(Guid Id, string Name, string DefaultUnitCode);

public sealed record ReviewUnitOption(Guid Id, string Code, string Name);

public sealed record ReviewLocationOption(Guid Id, string Name);

public sealed record ReviewCategoryOption(Guid Id, string Name);

public sealed record ReviewReferenceData(
    IReadOnlyList<ReviewProductOption> Products,
    IReadOnlyList<ReviewUnitOption> Units,
    IReadOnlyList<ReviewLocationOption> Locations,
    IReadOnlyList<ReviewCategoryOption> Categories);
