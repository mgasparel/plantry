using Plantry.Catalog.Domain;
using Plantry.Recipes.Application;

namespace Plantry.Web.Recipes;

/// <summary>
/// Web-side adapter for <see cref="IQuantityFormatter"/> — the one seam where Recipes' render surfaces
/// (Details, Cook) meet Catalog's pure <see cref="QuantityDisplay"/> formatter (quantity-display.md §7).
/// Loads the household's units once, then delegates the presentation to <see cref="QuantityFormatting"/>.
/// Lives in Plantry.Web (the composition root) so the Recipes projects never reference Catalog, mirroring
/// <see cref="RecipesUnitConverterAdapter"/>.
/// </summary>
public sealed class RecipesQuantityFormatterAdapter(IUnitRepository units) : IQuantityFormatter
{
    public async Task<IReadOnlyDictionary<string, FormattedQuantity>> FormatAsync(
        IReadOnlyList<QuantityFormatRequest> requests, CancellationToken ct = default)
    {
        if (requests.Count == 0)
            return new Dictionary<string, FormattedQuantity>();

        var allUnits = await units.ListAsync(ct);
        return QuantityFormatting.Format(requests, allUnits);
    }
}
