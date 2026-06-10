using Plantry.Pricing.Domain;

namespace Plantry.Pricing.Application;

public sealed class PricingQueries(IPriceObservationRepository repository)
{
    public Task<PriceObservation?> LatestPurchasePriceAsync(Guid productId, CancellationToken ct = default) =>
        repository.LatestForProductAsync(productId, ct);

    public Task<PriceObservation?> LatestSkuPriceAsync(Guid skuId, CancellationToken ct = default) =>
        repository.LatestForSkuAsync(skuId, ct);
}
