using Microsoft.EntityFrameworkCore;
using Plantry.Deals.Domain;

namespace Plantry.Deals.Infrastructure;

/// <summary>
/// EF-backed repository for the <see cref="FlyerImport"/> aggregate (P5-6). All queries run through
/// <see cref="DealsDbContext"/>'s household query filter (RLS-scoped), so the <c>(store, flyer_external_id)</c>
/// dedup lookup resolves only within the armed household — the third leg of the DD5 uniqueness key.
/// </summary>
public sealed class FlyerImportRepository(DealsDbContext db) : IFlyerImportRepository
{
    public Task<FlyerImport?> FindByDedupKeyAsync(Guid storeId, string flyerExternalId, CancellationToken ct = default) =>
        db.FlyerImports.FirstOrDefaultAsync(
            f => f.StoreId == storeId && f.FlyerExternalId == flyerExternalId, ct);

    public async Task AddAsync(FlyerImport import, CancellationToken ct = default) =>
        await db.FlyerImports.AddAsync(import, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
