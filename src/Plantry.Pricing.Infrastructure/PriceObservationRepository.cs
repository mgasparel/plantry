using Microsoft.EntityFrameworkCore;
using Plantry.Pricing.Domain;

namespace Plantry.Pricing.Infrastructure;

public sealed class PriceObservationRepository(PricingDbContext db) : IPriceObservationRepository
{
    public async Task AddAsync(PriceObservation observation, CancellationToken ct = default) =>
        await db.PriceObservations.AddAsync(observation, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);

    // Deliberately NOT superseded-filtered — the caller needs the row back even if it is already
    // superseded, so PriceObservation.Supersede can throw its own guard (ADR-023 A7).
    public Task<PriceObservation?> FindAsync(PriceObservationId id, CancellationToken ct = default) =>
        db.PriceObservations.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<PriceObservation>> ListPurchasesAwaitingStoreAsync(CancellationToken ct = default) =>
        await db.PriceObservations
            .Where(p => p.Source == PriceSource.Purchase
                && p.StoreId == null
                && p.MerchantText != null
                && p.MerchantText.Trim() != ""
                && p.SupersededById == null)
            .OrderBy(p => p.ObservedAt)
            .ToListAsync(ct);

    public Task<PriceObservation?> LatestForProductAsync(Guid productId, CancellationToken ct = default) =>
        db.PriceObservations
            .Where(p => p.ProductId == productId
                && (p.Source == PriceSource.Purchase || p.Source == PriceSource.Manual)
                && p.SupersededById == null)
            .OrderByDescending(p => p.ObservedAt)
            .FirstOrDefaultAsync(ct);

    public Task<PriceObservation?> LatestForSkuAsync(Guid skuId, CancellationToken ct = default) =>
        db.PriceObservations
            .Where(p => p.SkuId == skuId
                && (p.Source == PriceSource.Purchase || p.Source == PriceSource.Manual)
                && p.SupersededById == null)
            .OrderByDescending(p => p.ObservedAt)
            .FirstOrDefaultAsync(ct);

    public Task<PriceObservation?> CheapestActiveDealForProductAsync(Guid productId, DateOnly today, CancellationToken ct = default) =>
        db.PriceObservations
            .Where(p => p.ProductId == productId
                && p.Source == PriceSource.Deal
                && p.ValidFrom <= today
                && p.ValidTo >= today
                && p.SupersededById == null)
            .OrderBy(p => p.UnitPrice)
            .ThenBy(p => p.Price)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlySet<Guid>> ProductIdsWithAnyObservationAsync(
        IEnumerable<Guid> productIds, CancellationToken ct = default)
    {
        var idList = productIds.Distinct().ToList();
        if (idList.Count == 0)
            return new HashSet<Guid>();

        var found = await db.PriceObservations
            .Where(p => idList.Contains(p.ProductId) && p.SupersededById == null)
            .Select(p => p.ProductId)
            .Distinct()
            .ToListAsync(ct);
        return found.ToHashSet();
    }
}
