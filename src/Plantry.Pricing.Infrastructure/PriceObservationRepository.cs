using Microsoft.EntityFrameworkCore;
using Plantry.Pricing.Domain;

namespace Plantry.Pricing.Infrastructure;

public sealed class PriceObservationRepository(PricingDbContext db) : IPriceObservationRepository
{
    public async Task AddAsync(PriceObservation observation, CancellationToken ct = default) =>
        await db.PriceObservations.AddAsync(observation, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);

    public Task<PriceObservation?> LatestForProductAsync(Guid productId, CancellationToken ct = default) =>
        db.PriceObservations
            .Where(p => p.ProductId == productId)
            .OrderByDescending(p => p.ObservedAt)
            .FirstOrDefaultAsync(ct);

    public Task<PriceObservation?> LatestForSkuAsync(Guid skuId, CancellationToken ct = default) =>
        db.PriceObservations
            .Where(p => p.SkuId == skuId)
            .OrderByDescending(p => p.ObservedAt)
            .FirstOrDefaultAsync(ct);
}
