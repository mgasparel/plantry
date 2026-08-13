namespace Plantry.Pantry.Application;

/// <summary>Reads the active household default category for automatically produced products.</summary>
public interface IHouseholdDefaultProducedCategoryReader
{
    Task<Guid?> GetDefaultProducedCategoryIdAsync(CancellationToken ct = default);
}
