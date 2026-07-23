using System.Collections.Concurrent;
using Plantry.Housekeeping.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Web.Housekeeping;

/// <summary>
/// Process-local, in-memory implementation of <see cref="ITidyUpBadgeCache"/> (T6): a plain
/// per-household dictionary with a ~10 minute TTL, registered as a <b>singleton</b> so the count
/// survives across requests/scopes within this process. No persistence — a cold start has no entry for
/// any household until startup warmup or a miss-triggered background refresh populates one
/// (plantry-h0qq; see <see cref="TidyUpBadgeWarmup"/> / <see cref="TidyUpBadgeRefresher"/>).
/// <para>
/// <b>Stale-while-revalidate:</b> TTL expiry never removes an entry — it just flips
/// <see cref="TidyUpBadgeSnapshot.IsFresh"/> to false so the badge keeps showing the last known count
/// while a background refresh (miss/stale-triggered) catches it up. Only <see cref="InvalidateAsync"/>
/// (dismiss/restore, a real fact change) actually removes an entry.
/// </para>
/// </summary>
public sealed class TidyUpBadgeCache(IClock clock) : ITidyUpBadgeCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<Guid, (int Count, DateTimeOffset ExpiresAtUtc)> _entries = new();

    public Task<TidyUpBadgeSnapshot?> TryGetAsync(HouseholdId householdId, CancellationToken ct = default)
    {
        if (_entries.TryGetValue(householdId.Value, out var entry))
        {
            return Task.FromResult<TidyUpBadgeSnapshot?>(
                new TidyUpBadgeSnapshot(entry.Count, entry.ExpiresAtUtc > clock.UtcNow));
        }

        return Task.FromResult<TidyUpBadgeSnapshot?>(null);
    }

    public Task SetAsync(HouseholdId householdId, int count, CancellationToken ct = default)
    {
        _entries[householdId.Value] = (count, clock.UtcNow.Add(Ttl));
        return Task.CompletedTask;
    }

    public Task InvalidateAsync(HouseholdId householdId, CancellationToken ct = default)
    {
        _entries.TryRemove(householdId.Value, out _);
        return Task.CompletedTask;
    }
}
