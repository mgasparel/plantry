using Plantry.Pricing.Application;
using Plantry.Recipes.Application;
using Plantry.SharedKernel.Domain;

namespace Plantry.Web.Recipes;

/// <summary>
/// Web-side adapter for <see cref="IPriceReader"/> — supplies <see cref="Plantry.Recipes.Domain.CostingService"/>
/// with the <b>effective, costable (deal-aware) price</b> for a product by delegating to
/// <see cref="PricingQueries.EffectiveCostablePriceAsync"/>. Lives in Plantry.Web, the composition root that
/// already references the Pricing context, so the Recipes projects stay <c>→ SharedKernel only</c>.
///
/// Deal-aware costing (P5-9b, DJ6): <see cref="PricingQueries.EffectiveCostablePriceAsync"/> returns the
/// cheapest active in-window deal when one exists <b>and it has a usable unit</b>, else the latest purchase
/// (ADR-010: the effective-price read model lives in Pricing over <c>price_observation</c>; Recipes never
/// learns about Deals). A deal confirmed without a pack size (DM-17: empty unit, null unit price) is skipped
/// here so it never shadows a costable purchase and reads as "unpriced" (plantry-pxjp) — that same deal still
/// surfaces on display/sales-callout surfaces, which read <see cref="PricingQueries.EffectivePriceAsync"/>
/// instead. The deal window is evaluated against <see cref="IClock"/>-derived "today", so a deal silently
/// stops affecting cost once its window lapses — the price is computed per query, never stored.
///
/// The household scoping is enforced at the Postgres RLS level (ADR-008) — the
/// <c>HouseholdRlsConnectionInterceptor</c> arms <c>SET app.household_id</c> on the Pricing
/// connection before any query, so no additional household filter is required here.
/// </summary>
public sealed class PriceReaderAdapter(PricingQueries pricingQueries, IClock clock) : IPriceReader
{
    public async Task<PricePoint?> FindLatestAsync(Guid productId, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var observation = await pricingQueries.EffectiveCostablePriceAsync(productId, today, ct);
        if (observation is null)
            return null;

        return new PricePoint(
            observation.ProductId,
            observation.Price,
            observation.Quantity,
            observation.UnitId,
            observation.UnitPrice);
    }
}
