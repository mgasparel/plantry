using Plantry.Pantry.Application;
using Plantry.Recipes.Application;

namespace Plantry.Web.Recipes;

public sealed class HouseholdProducedCategoryReaderAdapter(
    IHouseholdDefaultProducedCategoryReader reader) : IHouseholdProducedCategoryReader
{
    public Task<Guid?> GetDefaultProducedCategoryIdAsync(CancellationToken ct = default) =>
        reader.GetDefaultProducedCategoryIdAsync(ct);
}
