using Plantry.SharedKernel;

namespace Plantry.Pricing.Domain;

public interface IPriceObservationRepository
{
    Task AddAsync(PriceObservation observation, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
    Task<PriceObservation?> LatestForProductAsync(Guid productId, CancellationToken ct = default);
    Task<PriceObservation?> LatestForSkuAsync(Guid skuId, CancellationToken ct = default);
}
