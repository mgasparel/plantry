namespace Plantry.Intake.Application;

/// <summary>
/// Port: loads the household's active product catalogue as AI hints for the receipt parser.
/// Implemented in Plantry.Web (reads from Catalog repositories).
/// </summary>
public interface ICatalogHintProvider
{
    Task<IReadOnlyList<ProductHint>> GetHintsAsync(CancellationToken ct = default);
}
