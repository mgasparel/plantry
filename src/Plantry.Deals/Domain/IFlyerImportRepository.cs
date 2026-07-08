namespace Plantry.Deals.Domain;

/// <summary>
/// A lightweight provenance tuple for one Parsed <see cref="FlyerImport"/> — the store, its run window, and
/// Flipp's <see cref="FlyerImport.FlyerExternalId"/> (the DD5 dedup anchor). Projected by
/// <see cref="IFlyerImportRepository.ListParsedRefsByStoresAsync"/> so the review queue can attach a
/// "View flyer" link to each flyer chapter (q9zr.7) without loading whole aggregates or an N+1. The external
/// id is carried through the projection for a future direct deep link; today's link is the store-search
/// fallback (direct flyer-slug URLs 404, verified 2026-07-07).
/// </summary>
public sealed record FlyerImportRef(Guid StoreId, DateOnly ValidFrom, DateOnly ValidTo, string FlyerExternalId);

/// <summary>
/// Read/write port for the <see cref="FlyerImport"/> aggregate (§4 / DJ2). RLS-scoped to the current
/// household by <c>DealsDbContext</c>, so every query returns only the signed-in household's rows. The
/// P5-6 <c>IngestFlyer</c> worker looks the import up by its dedup key — <c>(store_id, flyer_external_id)</c>
/// within the household (DD5) — to decide between a no-op (byte-identical content), a fresh import, or a
/// changed re-pull, and saves after each aggregate mutation so a mid-cycle crash leaves a consistent
/// import row.
/// </summary>
public interface IFlyerImportRepository
{
    /// <summary>
    /// The household's <b>Parsed</b> import for a <c>(store, flyer_external_id)</c> dedup key, or null if this
    /// flyer has never parsed successfully. Only <see cref="PullStatus.Parsed"/> rows occupy the dedup key
    /// (the partial unique index, plantry-0l05), so a Failed-only history returns null — the worker then does a
    /// clean fresh <see cref="FlyerImport.Start"/> and the retained Failed rows stay as audit. Household is
    /// enforced by the RLS query filter, so it is not a parameter (DD5).
    /// </summary>
    Task<FlyerImport?> FindParsedByDedupKeyAsync(Guid storeId, string flyerExternalId, CancellationToken ct = default);

    /// <summary>
    /// Batch-resolves the household's <b>Parsed</b> flyer imports for a set of stores, projected to the
    /// lightweight <see cref="FlyerImportRef"/> tuples the review queue needs to attach a "View flyer" link to
    /// each flyer chapter (q9zr.7) — one round trip, no N+1 (mirroring <c>ResolveNamesAsync</c>). Only
    /// <see cref="PullStatus.Parsed"/> rows are returned (a store with only a Failed history yields none), and
    /// all distinct windows for a store come back so the caller matches each chapter's own
    /// (store, validity-window) key. Household is enforced by the RLS query filter, so it is not a parameter (DD5).
    /// </summary>
    Task<IReadOnlyList<FlyerImportRef>> ListParsedRefsByStoresAsync(
        IReadOnlyList<Guid> storeIds, CancellationToken ct = default);

    Task AddAsync(FlyerImport import, CancellationToken ct = default);

    /// <summary>
    /// Detaches a single <see cref="FlyerImport"/> from the change tracker. Used only by the Failed-recording
    /// path (plantry-pwkm): after an atomic materialization transaction rolls back, the envelope saved inside it
    /// is left tracked as <c>Unchanged</c> — a phantom, since the row was rolled out of the database — which
    /// <see cref="IDealRepository.DiscardStagedChanges"/> (Added/Modified/Deleted only) deliberately does not
    /// touch. Detaching it frees the envelope's identity on the <c>(household_id, store_id, flyer_external_id)</c>
    /// dedup key so a fresh Pulling → Failed envelope reusing that provenance records without a unique collision.
    /// </summary>
    void Detach(FlyerImport import);

    /// <summary>
    /// Runs <paramref name="action"/> inside a single database transaction on the shared <c>DealsDbContext</c>,
    /// committing on success and rolling back on any exception (plantry-pwkm). One import's materialization —
    /// the <c>Pulling</c> envelope, its staged <c>Pending</c> <see cref="Deal"/>s, and the <c>Parsed</c>
    /// transition — is written through this seam so it commits <b>atomically or not at all</b>: a hard crash
    /// mid-write rolls back to nothing, so no partial <see cref="FlyerImport"/> row can wedge the
    /// <c>(household_id, store_id, flyer_external_id)</c> dedup key (DD5/DD15). The envelope must be INSERTed
    /// before its deals because the <c>deal → flyer_import</c> composite FK is enforced but has <b>no</b> EF
    /// navigation to order the inserts — hence an explicit transaction spanning two saves, not one save.
    /// </summary>
    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
