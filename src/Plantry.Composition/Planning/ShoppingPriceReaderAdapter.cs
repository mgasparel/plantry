using Plantry.Market.Application;
using Plantry.Planning.Application;

namespace Plantry.Web.Shopping;

/// <summary>
/// Web-side adapter for <see cref="IShoppingPriceReader"/> (plantry-e016) — supplies the shopping basket cost
/// estimate with each product's effective (deal-aware) price observation by delegating to Market's
/// <see cref="PricingQueries.EffectivePricesAsync"/> read model (ADR-010: Shopping never reads Market's
/// <c>price_observation</c> table directly). Mirrors <see cref="ShoppingDealReaderAdapter"/>'s ACL shape —
/// same underlying read model, cheapest active deal wins over latest purchase — but returns the raw
/// price/quantity/unit for line costing rather than the deal metadata (store name / deal id) the badge needs.
///
/// <para>Lives in Plantry.Web, the composition root that already references Market, so
/// Plantry.Planning.Application stays → SharedKernel only. Household scoping is enforced at the Postgres RLS
/// level (ADR-008) by the <c>HouseholdRlsConnectionInterceptor</c> on the Market connection, so no additional
/// household filter is needed here.</para>
/// </summary>
public sealed class ShoppingPriceReaderAdapter(PricingQueries pricing) : IShoppingPriceReader
{
    public async Task<IReadOnlyDictionary<Guid, ShoppingPriceEstimate>> GetEffectivePricesAsync(
        IReadOnlyList<Guid> productIds,
        DateOnly today,
        CancellationToken ct = default)
    {
        if (productIds.Count == 0)
            return new Dictionary<Guid, ShoppingPriceEstimate>();

        var observations = await pricing.EffectivePricesAsync(productIds, today, ct);
        return observations.ToDictionary(
            kv => kv.Key,
            kv => new ShoppingPriceEstimate(kv.Key, kv.Value.Price, kv.Value.Quantity, kv.Value.UnitId));
    }
}
