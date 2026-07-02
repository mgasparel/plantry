namespace Plantry.Deals.Domain;

/// <summary>
/// Read/write port for the <see cref="DealMatchMemory"/> aggregate (§6 / DD3). RLS-scoped to the current
/// household by <c>DealsDbContext</c>. The confirm orchestration (P5-5) upserts on the
/// <c>(household, store, normalized_name)</c> key — <see cref="FindByKeyAsync"/> then either
/// <see cref="AddAsync"/> a new memory or <c>Repoint</c> the existing one — so the upsert is idempotent
/// across re-drives.
/// </summary>
public interface IDealMatchMemoryRepository
{
    /// <summary>
    /// The household's memory for a <c>(store, normalized_name)</c> key, or null if none exists yet —
    /// the upsert lookup. Household is enforced by the RLS query filter, so it is not a parameter.
    /// </summary>
    Task<DealMatchMemory?> FindByKeyAsync(Guid storeId, string normalizedName, CancellationToken ct = default);

    Task AddAsync(DealMatchMemory memory, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
