using Plantry.Catalog.Domain;
using Plantry.Recipes.Application;
using Plantry.SharedKernel;

namespace Plantry.Web.Recipes;

/// <summary>
/// Web-side adapter for <see cref="IUnitConverter"/> — the one place Recipes' (and, from P2-2, Costing's)
/// conversion need meets Catalog's pure <see cref="UnitConverter"/> (DM-12). Loads the household's units
/// and the product's conversion overrides, then delegates the math; the loud no-path failure is
/// <see cref="UnitConverter"/>'s own. Lives in Plantry.Web so the Recipes projects never reference Catalog.
/// </summary>
public sealed class RecipesUnitConverterAdapter(IProductRepository products, IUnitRepository units)
    : IUnitConverter
{
    public async Task<Result<decimal>> ConvertAsync(
        Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default)
    {
        var allUnits = await units.ListAsync(ct);
        var product = await products.FindAsync(ProductId.From(productId), ct);
        IReadOnlyCollection<ProductConversion> conversions = product?.Conversions ?? [];

        return UnitConverter.Convert(amount, fromUnitId, toUnitId, allUnits, conversions);
    }
}
