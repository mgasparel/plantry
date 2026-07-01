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

    public Task<List<Deal>> ListByFlyerImportAsync(FlyerImportId flyerImportId, CancellationToken ct = default) =>
        db.Deals.Where(d => d.FlyerImportId == flyerImportId).ToListAsync(ct);

    public async Task AddAsync(Deal deal, CancellationToken ct = default) =>
        await db.Deals.AddAsync(deal, ct);

    public void Remove(Deal deal) => db.Deals.Remove(deal);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
