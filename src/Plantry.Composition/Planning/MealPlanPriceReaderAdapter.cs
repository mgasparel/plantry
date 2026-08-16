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
        found.TryGetValue(productId, out var product);
        var rollup = CreateContext(productId, product);
        var candidate = await EffectivePriceRollup.SelectAsync(pricingQueries, rollup,
            DateOnly.FromDateTime(clock.UtcNow.UtcDateTime),
            (id, amount, from, to, token) => converter.ConvertAsync(id, amount, from, to, token), ct);
        return candidate is null ? null : new MealPlanPricePoint(candidate.ConcreteProductId,
            candidate.Observation.Price, candidate.Observation.Quantity, candidate.Observation.UnitId,
            candidate.Observation.UnitPrice);
    }

    /// <summary>
    /// Rollup context for a requested id. A product absent from the catalog is a concrete leaf (self)
    /// with an unknown default unit — pre-DM-19 behaviour the meal-plan deal-aware costing tests rely on
    /// (a leaf needs no catalog round-trip to price). Mirrors <c>PriceReaderAdapter.CreateContext</c>.
    /// </summary>
    private static PriceRollupProduct CreateContext(Guid productId, CatalogProduct? product)
    {
        if (product is null)
            return new PriceRollupProduct(productId, Guid.Empty, IsParent: false, []);

        return new PriceRollupProduct(product.Id, product.DefaultUnitId, product.IsParent,
            product.VariantProductIds.Select(id => new PriceRollupVariant(id,
                product.VariantDefaultUnitIds?.GetValueOrDefault(id) ?? product.DefaultUnitId)).ToList());
    }
}
