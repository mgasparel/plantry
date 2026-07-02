using Plantry.SharedKernel;

namespace Plantry.Pricing.Domain;

public interface IPriceObservationRepository
{
    Task AddAsync(PriceObservation observation, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);

    /// <summary>Latest <c>purchase</c> observation for a product (source-filtered — a deal row never
    /// contaminates purchase costing).</summary>
    Task<PriceObservation?> LatestForProductAsync(Guid productId, CancellationToken ct = default);

    /// <summary>Latest <c>purchase</c> observation for a SKU (source-filtered).</summary>
    Task<PriceObservation?> LatestForSkuAsync(Guid skuId, CancellationToken ct = default);

    /// <summary>Cheapest active deal for a product: <c>source='deal'</c> whose validity window
    /// contains <paramref name="today"/>, lowest <c>unit_price</c> (DM-17 read model). Null when no
    /// deal is active.</summary>
    Task<PriceObservation?> CheapestActiveDealForProductAsync(Guid productId, DateOnly today, CancellationToken ct = default);
}
