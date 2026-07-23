using Plantry.Pricing.Domain;

namespace Plantry.Pricing.Application;

/// <summary>
/// Read-model projections over the append-only <see cref="PriceObservation"/> log (ADR-010: all price
/// read models live in Pricing; no other context reads the table). "Today" is supplied by the caller
/// (from its clock) so the active-deal window is evaluated deterministically.
/// </summary>
public sealed class PricingQueries(IPriceObservationRepository repository)
{
    public Task<PriceObservation?> LatestPurchasePriceAsync(Guid productId, CancellationToken ct = default) =>
        repository.LatestForProductAsync(productId, ct);

    public Task<PriceObservation?> LatestSkuPriceAsync(Guid skuId, CancellationToken ct = default) =>
        repository.LatestForSkuAsync(skuId, ct);

    /// <summary>Cheapest active deal for a product (DM-17): the in-window <c>source='deal'</c>
    /// observation with the lowest <c>unit_price</c>, evaluated against <paramref name="today"/>.
    /// Null when no deal is active.</summary>
    public Task<PriceObservation?> CheapestActiveDealAsync(Guid productId, DateOnly today, CancellationToken ct = default) =>
        repository.CheapestActiveDealForProductAsync(productId, today, ct);

    /// <summary>Effective price for a product (for deal-aware costing, P5-9b): the cheapest active deal
    /// if one exists, otherwise the latest purchase. Null when neither exists.</summary>
    public async Task<PriceObservation?> EffectivePriceAsync(Guid productId, DateOnly today, CancellationToken ct = default) =>
        await repository.CheapestActiveDealForProductAsync(productId, today, ct)
            ?? await repository.LatestForProductAsync(productId, ct);

    /// <summary>Effective price for <b>costing</b> callers only (plantry-pxjp): same precedence as
    /// <see cref="EffectivePriceAsync"/> — cheapest active deal, else latest purchase — except a deal
    /// recorded without a pack size (<see cref="PriceObservation.UnitId"/> == <see cref="Guid.Empty"/> or
    /// <see cref="PriceObservation.UnitPrice"/> null, DM-17's "confirmed without a pack size" soft-fail) is
    /// never returned: it has no usable unit for <see cref="Recipes.Domain.CostingService"/>'s unit
    /// conversion, so surfacing it here shadows a perfectly costable purchase observation and reads as
    /// "unpriced" instead. Falls back to the latest purchase in that case — never to a default unit
    /// (a default would mis-price, e.g. salmon fillets priced by weight vs salmon portions priced per-ea).
    /// Display/sales-callout surfaces (product detail, shopping list) must keep calling
    /// <see cref="EffectivePriceAsync"/>/<see cref="CheapestActiveDealAsync"/> — a unitless deal is still a
    /// real, worth-surfacing deal there; only costing needs a usable unit to convert against.</summary>
    public async Task<PriceObservation?> EffectiveCostablePriceAsync(Guid productId, DateOnly today, CancellationToken ct = default)
    {
        var deal = await repository.CheapestActiveDealForProductAsync(productId, today, ct);
        if (deal is not null && IsCostable(deal))
            return deal;

        return await repository.LatestForProductAsync(productId, ct);
    }

    /// <summary>A deal is usable for costing only when it carries both a real unit and a derived unit
    /// price (DM-17: a pack-sizeless deal confirmation writes <c>unitId = Guid.Empty</c> and a null
    /// <c>unit_price</c> soft-fail, by design — those rows have no conversion basis).</summary>
    private static bool IsCostable(PriceObservation observation) =>
        observation.UnitId != Guid.Empty && observation.UnitPrice.HasValue;

    /// <summary>Batch existence check for Tidy Up's D5 detector (tidy-up.md §3): of the given products,
    /// which have any live price observation at all, in one round trip.</summary>
    public Task<IReadOnlySet<Guid>> ProductIdsWithAnyPriceAsync(IEnumerable<Guid> productIds, CancellationToken ct = default) =>
        repository.ProductIdsWithAnyObservationAsync(productIds, ct);
}
