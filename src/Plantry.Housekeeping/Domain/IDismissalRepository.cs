using Plantry.SharedKernel;

namespace Plantry.Housekeeping.Domain;

/// <summary>
/// Read/write port for the <see cref="Dismissal"/> tombstone (tidy-up.md §4). RLS-scoped to the
/// current household by <c>HousekeepingDbContext</c>'s EF query filter (ADR-008), so every query
/// returns only the signed-in household's tombstones.
/// </summary>
public interface IDismissalRepository
{
    /// <summary>
    /// All of the household's tombstones in one round-trip — <see cref="GetTidyUpPageQuery"/>'s single
    /// batch load, matched in memory against every detector's findings rather than a query per finding.
    /// </summary>
    Task<IReadOnlyList<Dismissal>> ListForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default);

    /// <summary>The tombstone for one finding key, if any — regardless of its stored fingerprint.</summary>
    Task<Dismissal?> FindAsync(
        HouseholdId householdId, DetectorId detectorId, Guid subjectId, CancellationToken ct = default);

    Task AddAsync(Dismissal dismissal, CancellationToken ct = default);

    /// <summary>Deletes the tombstone — the whole of Restore (T5).</summary>
    Task RemoveAsync(Dismissal dismissal, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
