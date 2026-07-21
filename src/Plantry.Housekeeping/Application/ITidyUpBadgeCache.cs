using Plantry.SharedKernel;

namespace Plantry.Housekeeping.Application;

/// <summary>
/// The per-household cached open-finding count backing the nav badge (T6). Implemented as an
/// in-memory, process-local cache in <c>Plantry.Web</c> (no persistence — a cold start or a second
/// instance simply recomputes on first Tidy Up visit). The layout reads only <see cref="TryGetAsync"/>
/// and never runs detectors; <see cref="GetTidyUpPageQuery"/> is the only writer of a fresh count via
/// <see cref="SetAsync"/>, and dismiss/restore call <see cref="InvalidateAsync"/> so a stale count
/// never outlives a user action that changed it (falling back to 0 — no badge — until the next
/// Tidy Up render repopulates it).
/// </summary>
public interface ITidyUpBadgeCache
{
    /// <summary>The cached count, or null when absent/expired (~10 min TTL, T6) — never computes a fresh value.</summary>
    Task<int?> TryGetAsync(HouseholdId householdId, CancellationToken ct = default);

    /// <summary>Refreshes the cache with a freshly computed count (called after every real detector run).</summary>
    Task SetAsync(HouseholdId householdId, int count, CancellationToken ct = default);

    /// <summary>Drops the cached count so the next read/render recomputes it (dismiss/restore, T6).</summary>
    Task InvalidateAsync(HouseholdId householdId, CancellationToken ct = default);
}
