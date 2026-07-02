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
}
