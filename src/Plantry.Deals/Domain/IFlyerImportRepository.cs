namespace Plantry.Deals.Domain;

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
    /// The household's import for a <c>(store, flyer_external_id)</c> dedup key, or null if this flyer has
    /// never been pulled. Household is enforced by the RLS query filter, so it is not a parameter (DD5).
    /// </summary>
    Task<FlyerImport?> FindByDedupKeyAsync(Guid storeId, string flyerExternalId, CancellationToken ct = default);

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
