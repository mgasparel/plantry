using Plantry.Planning.Application;
using Plantry.Market.Application;
using Plantry.SharedKernel.Domain;
using Plantry.Recipes.Application;
using Plantry.Web.Market;

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
        found.TryGetValue(productId, out var product);
        var rollup = CreateContext(productId, product);
        var candidate = await EffectivePriceRollup.SelectAsync(pricingQueries, rollup,
            DateOnly.FromDateTime(clock.UtcNow.UtcDateTime),
            (id, amount, from, to, token) => converter.ConvertAsync(id, amount, from, to, token), ct);
        return candidate is null ? null : new MealPlanPricePoint(candidate.ConcreteProductId,
            candidate.Observation.Price, candidate.Observation.Quantity, candidate.Observation.UnitId,
            candidate.Observation.UnitPrice);
    }

    /// <summary>Rollup context for a requested id — see <see cref="PriceRollupContextBuilder"/>.</summary>
    private static PriceRollupProduct CreateContext(Guid productId, CatalogProduct? product) =>
        PriceRollupContextBuilder.Build(productId, product);
}
