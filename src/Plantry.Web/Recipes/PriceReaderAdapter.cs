using Plantry.Pricing.Application;
using Plantry.Recipes.Application;

namespace Plantry.Web.Recipes;

/// <summary>
/// Web-side adapter for <see cref="IPriceReader"/> — supplies <see cref="Plantry.Recipes.Domain.CostingService"/>
/// with the latest <c>PriceObservation</c> for a product by delegating to <see cref="PricingQueries"/>.
/// Lives in Plantry.Web, the composition root that already references the Pricing context, so the
/// Recipes projects stay <c>→ SharedKernel only</c>.
///
/// The household scoping is enforced at the Postgres RLS level (ADR-008) — the
/// <c>HouseholdRlsConnectionInterceptor</c> arms <c>SET app.household_id</c> on the Pricing
/// connection before any query, so no additional household filter is required here.
/// </summary>
public sealed class PriceReaderAdapter(PricingQueries pricingQueries) : IPriceReader
{
    public async Task<PricePoint?> FindLatestAsync(Guid productId, CancellationToken ct = default)
    {
        var observation = await pricingQueries.LatestPurchasePriceAsync(productId, ct);
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
