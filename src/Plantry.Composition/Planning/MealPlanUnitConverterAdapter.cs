using Plantry.Catalog.Domain;
using Plantry.Planning.Application;
using Plantry.SharedKernel;

namespace Plantry.Web.MealPlanning;

/// <summary>
/// Web-side adapter for <see cref="IMealPlanUnitConverter"/> (plantry-9n7l) — mirrors
/// <see cref="Plantry.Web.Recipes.RecipesUnitConverterAdapter"/> exactly: loads the household's units
/// and the product's conversion overrides, then delegates the math to Catalog's pure
/// <see cref="UnitConverter"/>. A second, MealPlanning-owned copy of the adapter (not a shared class)
/// so neither context depends on the other (DM-3). Lives in Plantry.Web so the MealPlanning projects
/// never reference Catalog.
/// </summary>
public sealed class MealPlanUnitConverterAdapter(IProductRepository products, IUnitRepository units)
    : IMealPlanUnitConverter
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
