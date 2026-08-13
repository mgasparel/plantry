using Plantry.Planning.Application;
using Plantry.Market.Application;
using Plantry.SharedKernel.Domain;
using Plantry.Recipes.Application;

namespace Plantry.Web.MealPlanning;

public sealed class MealPlanPriceReaderAdapter(
    PricingQueries pricingQueries,
    IClock clock,
    Plantry.Recipes.Application.ICatalogProductReader catalog,
    IUnitConverter converter) : IMealPlanPriceReader
{
    public async Task<MealPlanPricePoint?> FindLatestAsync(Guid productId, CancellationToken ct = default)
    {
        var found = await catalog.FindManyWithVariantsAsync([productId], ct);
        if (!found.TryGetValue(productId, out var product)) return null;
        var rollup = new PriceRollupProduct(product.Id, product.DefaultUnitId, product.IsParent,
            product.VariantProductIds.Select(v => new PriceRollupVariant(v,
                product.VariantDefaultUnitIds?.GetValueOrDefault(v) ?? product.DefaultUnitId)).ToList());
        var candidate = await EffectivePriceRollup.SelectAsync(pricingQueries, rollup,
            DateOnly.FromDateTime(clock.UtcNow.UtcDateTime),
            (id, amount, from, to, token) => converter.ConvertAsync(id, amount, from, to, token), ct);
        return candidate is null ? null : new MealPlanPricePoint(candidate.ConcreteProductId,
            candidate.Observation.Price, candidate.ConvertedQuantity, candidate.RequestedUnitId,
            candidate.ConvertedUnitPrice);
    }
}
