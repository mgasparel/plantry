using Plantry.MealPlanning.Application;
using Plantry.Pricing.Application;

namespace Plantry.Web.MealPlanning;

/// <summary>
/// Web-side adapter for <see cref="IMealPlanPriceReader"/> — delegates to the existing
/// <see cref="PricingQueries.LatestPurchasePriceAsync"/> used by the Recipes <c>PriceReaderAdapter</c>.
/// Lives in Plantry.Web (the composition root) to keep MealPlanning free of Pricing dependencies.
/// Deal-blind in Phase 3 (C7) — only purchase-price history is used.
/// All identifiers cross as raw <see cref="Guid"/> soft refs (DM-3).
/// </summary>
public sealed class MealPlanPriceReaderAdapter(PricingQueries pricingQueries) : IMealPlanPriceReader
{
    public async Task<MealPlanPricePoint?> FindLatestAsync(Guid productId, CancellationToken ct = default)
    {
        var observation = await pricingQueries.LatestPurchasePriceAsync(productId, ct);
        if (observation is null) return null;

        return new MealPlanPricePoint(
            observation.ProductId,
            observation.Price,
            observation.Quantity,
            observation.UnitId,
            observation.UnitPrice);
    }
}
