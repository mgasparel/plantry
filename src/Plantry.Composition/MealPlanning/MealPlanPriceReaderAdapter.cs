using Plantry.MealPlanning.Application;
using Plantry.Pricing.Application;
using Plantry.SharedKernel.Domain;

namespace Plantry.Web.MealPlanning;

/// <summary>
/// Web-side adapter for <see cref="IMealPlanPriceReader"/> — delegates to the same
/// <see cref="PricingQueries.EffectiveCostablePriceAsync"/> read used by the Recipes <c>PriceReaderAdapter</c>,
/// so both cost paths share identical deal-aware semantics. Lives in Plantry.Web (the composition root)
/// to keep MealPlanning free of Pricing dependencies.
/// Deal-aware costing (P5-9b, DJ6): the effective price is the cheapest active in-window deal when one
/// exists <b>and it has a usable unit</b>, else the latest purchase (ADR-010: the read model lives in
/// Pricing over <c>price_observation</c>; MealPlanning never learns about Deals). A deal confirmed without a
/// pack size (DM-17: empty unit, null unit price) is skipped here so it never shadows a costable purchase
/// and reads as "unpriced" (plantry-pxjp); that same deal still surfaces on display/sales-callout surfaces,
/// which read <see cref="PricingQueries.EffectivePriceAsync"/> instead. The deal window is evaluated against
/// <see cref="IClock"/>-derived "today", so a deal silently stops affecting cost/weighting once its window
/// lapses — computed per query, never stored.
/// All identifiers cross as raw <see cref="Guid"/> soft refs (DM-3).
/// </summary>
public sealed class MealPlanPriceReaderAdapter(PricingQueries pricingQueries, IClock clock) : IMealPlanPriceReader
{
    public async Task<MealPlanPricePoint?> FindLatestAsync(Guid productId, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var observation = await pricingQueries.EffectiveCostablePriceAsync(productId, today, ct);
        if (observation is null) return null;

        return new MealPlanPricePoint(
            observation.ProductId,
            observation.Price,
            observation.Quantity,
            observation.UnitId,
            observation.UnitPrice);
    }
}
