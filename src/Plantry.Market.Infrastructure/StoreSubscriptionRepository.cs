using Microsoft.EntityFrameworkCore;
using Plantry.Market.Domain;

namespace Plantry.Market.Infrastructure;

/// <summary>
/// EF-backed repository for the <see cref="StoreSubscription"/> aggregate (P5-2). All queries run through
/// <see cref="MarketDbContext"/>'s household query filter (RLS-scoped), so reads never cross tenants.
/// </summary>
public sealed class StoreSubscriptionRepository(MarketDbContext db) : IStoreSubscriptionRepository
{
    public Task<StoreSubscription?> FindAsync(StoreSubscriptionId id, CancellationToken ct = default) =>
        db.StoreSubscriptions.FirstOrDefaultAsync(s => s.Id == id, ct);

    public Task<StoreSubscription?> FindByStoreAsync(Guid storeId, CancellationToken ct = default) =>
        db.StoreSubscriptions.FirstOrDefaultAsync(s => s.StoreId == storeId, ct);

    public Task<List<StoreSubscription>> ListAsync(CancellationToken ct = default) =>
        db.StoreSubscriptions.OrderBy(s => s.CreatedAt).ToListAsync(ct);

    public Task<List<StoreSubscription>> ListActiveAsync(CancellationToken ct = default) =>
        db.StoreSubscriptions.Where(s => s.IsActive).OrderBy(s => s.CreatedAt).ToListAsync(ct);

    // Cross-tenant: IgnoreQueryFilters lifts the app-layer household filter, mirroring
    // HouseholdRepository.ListAllIdsAsync. The Postgres RLS policy (household_isolation, extended by the
    // AllowCrossHouseholdStoreSubscriptionRead migration) still guards the row set and only exposes every
    // household's rows when app.household_id is unset — see the port contract.
    public Task<DateTimeOffset?> GetLastPulledAtAcrossHouseholdsAsync(CancellationToken ct = default) =>
        db.StoreSubscriptions
            .IgnoreQueryFilters()
            .MaxAsync(s => (DateTimeOffset?)s.LastPulledAt, ct);

    public async Task AddAsync(StoreSubscription subscription, CancellationToken ct = default) =>
        await db.StoreSubscriptions.AddAsync(subscription, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
