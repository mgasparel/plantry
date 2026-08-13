namespace Plantry.Recipes.Application;

/// <summary>Recipes ACL for the current household's active default produced-product category.</summary>
public interface IHouseholdProducedCategoryReader
{
    Task<Guid?> GetDefaultProducedCategoryIdAsync(CancellationToken ct = default);
}
