using Microsoft.EntityFrameworkCore;
using Plantry.Pricing.Domain;

namespace Plantry.Pricing.Infrastructure;

public sealed class PriceObservationRepository(PricingDbContext db) : IPriceObservationRepository
{
    public async Task AddAsync(PriceObservation observation, CancellationToken ct = default) =>
        await db.PriceObservations.AddAsync(observation, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);

    public async Task<IReadOnlyList<PriceObservation>> ListPurchasesAwaitingStoreAsync(CancellationToken ct = default) =>
        await db.PriceObservations
            .Where(p => p.Source == PriceSource.Purchase
                && p.StoreId == null
                && p.MerchantText != null
                && p.MerchantText.Trim() != "")
            .OrderBy(p => p.ObservedAt)
            .ToListAsync(ct);

    public Task<PriceObservation?> LatestForProductAsync(Guid productId, CancellationToken ct = default) =>
        db.PriceObservations
            .Where(p => p.ProductId == productId && p.Source == PriceSource.Purchase)
            .OrderByDescending(p => p.ObservedAt)
            .FirstOrDefaultAsync(ct);

    public Task<PriceObservation?> LatestForSkuAsync(Guid skuId, CancellationToken ct = default) =>
        db.PriceObservations
            .Where(p => p.SkuId == skuId && p.Source == PriceSource.Purchase)
            .OrderByDescending(p => p.ObservedAt)
            .FirstOrDefaultAsync(ct);

    public Task<PriceObservation?> CheapestActiveDealForProductAsync(Guid productId, DateOnly today, CancellationToken ct = default) =>
        db.PriceObservations
            .Where(p => p.ProductId == productId
                && p.Source == PriceSource.Deal
                && p.ValidFrom <= today
                && p.ValidTo >= today)
            .OrderBy(p => p.UnitPrice)
            .ThenBy(p => p.Price)
            .FirstOrDefaultAsync(ct);
}
