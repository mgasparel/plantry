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

    Task SaveChangesAsync(CancellationToken ct = default);
}
