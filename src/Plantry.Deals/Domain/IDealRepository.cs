namespace Plantry.Deals.Domain;

/// <summary>
/// Read/write port for the <see cref="Deal"/> aggregate (§5 / DJ4). RLS-scoped to the current household
/// by <c>DealsDbContext</c>, so every query returns only the signed-in household's rows. The confirm /
/// reject orchestration (P5-5) saves after <b>each</b> aggregate mutation so its cross-context commit is
/// resumable — see <c>ConfirmDeal</c>.
/// </summary>
public interface IDealRepository
{
    Task<Deal?> FindAsync(DealId id, CancellationToken ct = default);

    /// <summary>
    /// All deals materialized from a given <see cref="FlyerImport"/> (the P5-6 re-pull path, DD13). The
    /// worker partitions these into still-<see cref="DealStatus.Pending"/> deals (refreshed on a changed
    /// re-pull) and resolved <see cref="DealStatus.Confirmed"/>/<see cref="DealStatus.Rejected"/> deals
    /// (frozen — never overwritten). RLS-scoped, so it never crosses households.
    /// </summary>
    Task<List<Deal>> ListByFlyerImportAsync(FlyerImportId flyerImportId, CancellationToken ct = default);

    Task AddAsync(Deal deal, CancellationToken ct = default);

    /// <summary>
    /// Removes a deal. Used only by the re-pull refresh (DD13) to drop a superseded still-Pending deal
    /// before re-staging the flyer's current items; a resolved deal is never removed (it is frozen).
    /// </summary>
    void Remove(Deal deal);

    Task SaveChangesAsync(CancellationToken ct = default);
}
