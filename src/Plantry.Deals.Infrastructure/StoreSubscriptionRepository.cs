using Microsoft.EntityFrameworkCore;
using Plantry.Deals.Domain;

namespace Plantry.Deals.Infrastructure;

/// <summary>
/// EF-backed repository for the <see cref="StoreSubscription"/> aggregate (P5-2). All queries run through
/// <see cref="DealsDbContext"/>'s household query filter (RLS-scoped), so reads never cross tenants.
/// </summary>
public sealed class StoreSubscriptionRepository(DealsDbContext db) : IStoreSubscriptionRepository
{
    public Task<StoreSubscription?> FindAsync(StoreSubscriptionId id, CancellationToken ct = default) =>
        db.StoreSubscriptions.FirstOrDefaultAsync(s => s.Id == id, ct);

    public Task<StoreSubscription?> FindByStoreAsync(Guid storeId, CancellationToken ct = default) =>
        db.StoreSubscriptions.FirstOrDefaultAsync(s => s.StoreId == storeId, ct);

    public Task<List<StoreSubscription>> ListAsync(CancellationToken ct = default) =>
        db.StoreSubscriptions.OrderBy(s => s.CreatedAt).ToListAsync(ct);

    public Task<List<StoreSubscription>> ListActiveAsync(CancellationToken ct = default) =>
        db.StoreSubscriptions.Where(s => s.IsActive).OrderBy(s => s.CreatedAt).ToListAsync(ct);

    public async Task AddAsync(StoreSubscription subscription, CancellationToken ct = default) =>
        await db.StoreSubscriptions.AddAsync(subscription, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
