using Microsoft.EntityFrameworkCore;
using Plantry.Deals.Domain;

namespace Plantry.Deals.Infrastructure;

/// <summary>
/// EF-backed repository for the <see cref="Deal"/> aggregate (P5-5). All queries run through
/// <see cref="DealsDbContext"/>'s household query filter (RLS-scoped), so reads never cross tenants.
/// <see cref="SaveChangesAsync"/> is called after each aggregate mutation to keep the confirm/reject
/// orchestration resumable.
/// </summary>
public sealed class DealRepository(DealsDbContext db) : IDealRepository
{
    public Task<Deal?> FindAsync(DealId id, CancellationToken ct = default) =>
        db.Deals.FirstOrDefaultAsync(d => d.Id == id, ct);

    public Task<List<Deal>> ListBrowsableAsync(CancellationToken ct = default) =>
        db.Deals
            .Where(d => d.Status == DealStatus.Pending || d.Status == DealStatus.Confirmed)
            .ToListAsync(ct);

    public Task<List<Deal>> ListByFlyerImportAsync(FlyerImportId flyerImportId, CancellationToken ct = default) =>
        db.Deals.Where(d => d.FlyerImportId == flyerImportId).ToListAsync(ct);

    public async Task AddAsync(Deal deal, CancellationToken ct = default) =>
        await db.Deals.AddAsync(deal, ct);

    public void Remove(Deal deal) => db.Deals.Remove(deal);

    public void DiscardStagedChanges()
    {
        // Detach every uncommitted entry (Added / Modified / Deleted). EF keeps entities tracked when a
        // SaveChanges throws, so a faulted subscription would otherwise strand its Added/Deleted Deal rows
        // in the shared context and flush them alongside the next subscription's commit (plantry-60p9).
        // Unchanged entities — including the ingest loop's per-iteration-committed StoreSubscriptions — are
        // deliberately left tracked, so this drops nothing legitimate on the success path.
        var staged = db.ChangeTracker
            .Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        foreach (var entry in staged)
            entry.State = EntityState.Detached;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
