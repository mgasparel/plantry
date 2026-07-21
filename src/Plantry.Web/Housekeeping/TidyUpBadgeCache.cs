using System.Collections.Concurrent;
using Plantry.Housekeeping.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Web.Housekeeping;

/// <summary>
/// Process-local, in-memory implementation of <see cref="ITidyUpBadgeCache"/> (T6): a plain
/// per-household dictionary with a ~10 minute TTL, registered as a <b>singleton</b> so the count
/// survives across requests/scopes within this process. No persistence — a cold start or a second web
/// instance simply shows no badge until the household's next Tidy Up page render repopulates it, which
/// the design explicitly accepts ("a briefly-stale count is fine: the page itself is always truthful").
/// </summary>
public sealed class TidyUpBadgeCache(IClock clock) : ITidyUpBadgeCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<Guid, (int Count, DateTimeOffset ExpiresAtUtc)> _entries = new();

    public Task<int?> TryGetAsync(HouseholdId householdId, CancellationToken ct = default)
    {
        if (_entries.TryGetValue(householdId.Value, out var entry) && entry.ExpiresAtUtc > clock.UtcNow)
            return Task.FromResult<int?>(entry.Count);

        return Task.FromResult<int?>(null);
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
