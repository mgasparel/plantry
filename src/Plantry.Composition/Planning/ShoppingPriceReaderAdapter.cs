using Plantry.Market.Application;
using Plantry.Market.Domain;
using Plantry.Planning.Application;
using Plantry.SharedKernel;

namespace Plantry.Web.Shopping;

/// <summary>
/// Web-side adapter for <see cref="IShoppingPriceReader"/> (plantry-e016) — supplies the shopping basket cost
/// estimate with each product's effective, costable (deal-aware) price observation by delegating to Market's
/// <see cref="PricingQueries.EffectiveCostablePricesAsync"/> read model (ADR-010: Shopping never reads
/// Market's <c>price_observation</c> table directly). This is a costing consumer, so a deal recorded without
/// a pack size (DM-17's "confirmed without a pack size" soft-fail) falls through to the latest purchase
/// instead of being surfaced — a unitless deal has no usable unit for <see cref="Plantry.Planning.Domain.Shopping.ShoppingBasketCostingService"/>'s
/// unit conversion. Mirrors <see cref="ShoppingDealReaderAdapter"/>'s ACL shape — same underlying read
/// model, cheapest active deal wins over latest purchase — but returns the raw price/quantity/unit for line
/// costing rather than the deal metadata (store name / deal id) the badge needs.
///
/// <para><b>Parent-aware (plantry-oh27.4):</b> a requested id may be a parent product ("Bubly" on the
/// shopping list). Parents own no price observations of their own (epic constraint) — their price is the
/// cheapest live variant's observation, converted into the parent's default unit, via the shared
/// <see cref="EffectivePriceRollup"/> policy (mirrors <c>MealPlanPriceReaderAdapter</c> / <c>PriceReaderAdapter</c>,
/// the same rollup meal-plan and recipe costing already use). Every leaf id AND every live variant of every
/// requested parent are folded into ONE <see cref="PricingQueries.EffectiveCostablePricesAsync"/> call — a
/// single batched round trip for the whole basket regardless of how many parents are on the list — then each
/// parent is resolved against that shared observation set via
/// <see cref="EffectivePriceRollup.SelectFromObservationsAsync"/> (no further DB access per parent).</para>
///
/// <para>Lives in Plantry.Web, the composition root that already references Market, so
/// Plantry.Planning.Application stays → SharedKernel only. Household scoping is enforced at the Postgres RLS
/// level (ADR-008) by the <c>HouseholdRlsConnectionInterceptor</c> on the Market connection, so no additional
/// household filter is needed here.</para>
/// </summary>
public sealed class ShoppingPriceReaderAdapter(PricingQueries pricing, IShoppingCatalogReader catalog) : IShoppingPriceReader
{
    public async Task<IReadOnlyDictionary<Guid, ShoppingPriceEstimate>> GetEffectivePricesAsync(
        IReadOnlyList<Guid> productIds,
        DateOnly today,
        CancellationToken ct = default)
    {
        if (productIds.Count == 0)
            return new Dictionary<Guid, ShoppingPriceEstimate>();

        var families = await catalog.ResolveFamilyAsync(productIds, ct);

        var leafIds = new List<Guid>();
        var parentIds = new List<Guid>();
        foreach (var id in productIds.Distinct())
        {
            // An id absent from the catalog family map (e.g. deleted since the item was added) is
            // treated as a leaf — the direct lookup below simply finds no observation for it.
            if (families.TryGetValue(id, out var family) && family.IsParent)
                parentIds.Add(id);
            else
                leafIds.Add(id);
        }

        // One batched observation fetch for the WHOLE basket: every leaf id, plus every live variant of
        // every requested parent — never a per-parent round trip (the N+1 EffectivePriceRollup.SelectAsync
        // would otherwise cause, since it issues its own EffectiveCostablePricesAsync call internally).
        var allObservationIds = leafIds
            .Concat(parentIds.SelectMany(id => families[id].Variants.Select(v => v.ProductId)))
            .Distinct()
            .ToList();
        var observations = allObservationIds.Count > 0
            ? await pricing.EffectiveCostablePricesAsync(allObservationIds, today, ct)
            : new Dictionary<Guid, PriceObservation>();

        var result = new Dictionary<Guid, ShoppingPriceEstimate>();

        foreach (var id in leafIds)
        {
            if (observations.TryGetValue(id, out var observation))
                result[id] = new ShoppingPriceEstimate(id, observation.Price, observation.Quantity, observation.UnitId);
        }

        foreach (var parentId in parentIds)
        {
            var family = families[parentId];
            var rollupProduct = new PriceRollupProduct(
                family.ProductId,
                family.DefaultUnitId,
                IsParent: true,
                family.Variants.Select(v => new PriceRollupVariant(v.ProductId, v.DefaultUnitId)).ToList());

            var candidate = await EffectivePriceRollup.SelectFromObservationsAsync(
                rollupProduct, observations, ConvertAsync, ct);

            if (candidate is not null)
                result[parentId] = new ShoppingPriceEstimate(
                    parentId, candidate.Observation.Price, candidate.ConvertedQuantity, candidate.RequestedUnitId);
        }

        return result;
    }

    /// <summary>
    /// Adapts <see cref="IShoppingCatalogReader.TryConvertAsync"/> (Shopping's own unit-conversion ACL,
    /// already used by <c>ShoppingBasketCostingService</c>) into the
    /// <c>Func&lt;Guid, decimal, Guid, Guid, CancellationToken, Task&lt;Result&lt;decimal&gt;&gt;&gt;</c> shape
    /// <see cref="EffectivePriceRollup.SelectAsync"/> expects, rather than pulling in Recipes' <c>IUnitConverter</c> —
    /// Shopping already has everything it needs for this conversion via its existing Catalog ACL port.
    /// </summary>
    private async Task<Result<decimal>> ConvertAsync(
        Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct)
    {
        var converted = await catalog.TryConvertAsync(amount, fromUnitId, toUnitId, productId, ct);
        return converted is { } value ? Result<decimal>.Success(value) : Result<decimal>.Failure(Error.NotFound);
    }
}
