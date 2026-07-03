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
    /// Every <see cref="DealStatus.Pending"/> or <see cref="DealStatus.Confirmed"/> deal for the household
    /// (Rejected excluded) — the source set for the <c>BrowseDeals</c> read model (P5-7, DJ3). The
    /// clock-driven in-window partition (active = Confirmed ∧ in-window, DD7; pending = Pending ∧
    /// today ≤ valid_to, DD14) is applied by the read service against <c>IClock</c>, never persisted here.
    /// RLS-scoped, so it never crosses households.
    /// </summary>
    Task<List<Deal>> ListBrowsableAsync(CancellationToken ct = default);

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

    /// <summary>
    /// Per-subscription unit-of-work reset for the ingest cycle (P5-6 isolation, plantry-60p9). Discards
    /// any <b>uncommitted</b> changes staged in the shared <c>DealsDbContext</c> — every entity currently
    /// tracked as <c>Added</c>, <c>Modified</c>, or <c>Deleted</c> is detached. The ingest worker calls
    /// this at each subscription boundary so that a <see cref="SaveChangesAsync"/> fault in one
    /// subscription — which leaves its <c>Added</c>/<c>Deleted</c> <see cref="Deal"/> rows tracked, since
    /// EF does <b>not</b> detach on a failed save — cannot ride into the next subscription's commit
    /// (inserting the failed flyer's deals and/or deleting a household's prior Pending deals meant only to
    /// be replaced). Already-committed (<c>Unchanged</c>) entities are left tracked and untouched — notably
    /// the loop's <c>StoreSubscription</c> working set, whose <c>RecordPull</c> write is committed per
    /// iteration and must survive the reset — so a boundary reset drops nothing legitimate on the success
    /// path and exactly the stranded partial changes on the failed path.
    /// </summary>
    void DiscardStagedChanges();

    Task SaveChangesAsync(CancellationToken ct = default);
}
