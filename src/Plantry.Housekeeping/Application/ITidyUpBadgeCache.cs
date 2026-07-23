using Plantry.SharedKernel;

namespace Plantry.Housekeeping.Application;

/// <summary>
/// The per-household cached open-finding count backing the nav badge (T6). Implemented as an
/// in-memory, process-local cache in <c>Plantry.Web</c> (no persistence — a cold start has no entry
/// until warmup or a miss-triggered refresh populates one). The layout reads only <see cref="TryGetAsync"/>
/// and never runs detectors; <see cref="GetTidyUpPageQuery"/> is the only writer of a fresh count via
/// <see cref="SetAsync"/>, and dismiss/restore call <see cref="InvalidateAsync"/> so a stale count
/// never outlives a user action that changed it.
/// </summary>
public interface ITidyUpBadgeCache
{
    /// <summary>
    /// The cached count plus a freshness signal, or null only when truly absent (never set, or since
    /// invalidated — dismiss/restore, T5). <b>Stale-while-revalidate</b> (plantry-h0qq): TTL expiry
    /// (~10 min, T6) marks an entry stale rather than removing it, so the badge keeps rendering the
    /// last known count instead of blinking off while a background refresh (<c>TidyUpBadgeRefresher</c>
    /// in <c>Plantry.Web</c>) catches it up. Never computes a fresh value itself.
    /// </summary>
    Task<TidyUpBadgeSnapshot?> TryGetAsync(HouseholdId householdId, CancellationToken ct = default);

    /// <summary>Refreshes the cache with a freshly computed, fresh count (called after every real detector run).</summary>
    Task SetAsync(HouseholdId householdId, int count, CancellationToken ct = default);

    /// <summary>Drops the cached count so the next read/render recomputes it (dismiss/restore, T6).</summary>
    Task InvalidateAsync(HouseholdId householdId, CancellationToken ct = default);
}

/// <summary>
/// A cached badge read (plantry-h0qq SWR). <paramref name="IsFresh"/> is false once the ~10 min TTL has
/// elapsed since the last <see cref="ITidyUpBadgeCache.SetAsync"/> — the count is still the last real
/// value, just old enough that a caller should also request a background refresh
/// (<c>TidyUpBadgeRefresher</c>) rather than trust it indefinitely.
/// </summary>
public sealed record TidyUpBadgeSnapshot(int Count, bool IsFresh);
