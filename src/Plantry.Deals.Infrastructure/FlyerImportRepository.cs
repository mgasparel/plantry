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

    public void Detach(FlyerImport import) => db.Entry(import).State = EntityState.Detached;

    public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken ct = default)
    {
        // Explicit transaction on the shared DealsDbContext so an import's whole materialization commits
        // atomically (plantry-pwkm). Two saves live inside it — the FlyerImport INSERT then its deals — because
        // the deal → flyer_import composite FK is enforced yet unmodelled in EF, so EF cannot order the inserts
        // within a single save. On any exception the transaction is disposed without a commit and Postgres rolls
        // the whole write back (no partial, still-Pulling import survives); the exception then propagates to the
        // caller's Failed-recording path.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await action(ct);
        await tx.CommitAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
