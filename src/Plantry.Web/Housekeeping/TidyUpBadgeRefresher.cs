using System.Collections.Concurrent;
using Plantry.Catalog.Infrastructure;
using Plantry.Housekeeping.Application;
using Plantry.Housekeeping.Infrastructure;
using Plantry.Inventory.Infrastructure;
using Plantry.Pricing.Infrastructure;
using Plantry.Recipes.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Background;

namespace Plantry.Web.Housekeeping;

/// <summary>
/// Single-flight background refresher for the T6 badge cache (plantry-h0qq). The layout / More hub read
/// path calls <see cref="RequestRefreshAsync"/> whenever <see cref="ITidyUpBadgeCache.TryGetAsync"/>
/// comes back missing or stale; this enqueues at most one pending recompute per household onto
/// <see cref="IBackgroundTaskQueue"/> — a per-household in-flight guard means N concurrent misses/stale
/// reads for the same household produce exactly one enqueued work item, not N.
/// <para>
/// The work item creates its own fresh DI scope (via the queue/<c>QueuedHostedService</c>, plantry-qll2.4)
/// and arms tenancy with no ambient HTTP request, exactly as <c>RecipeConversionBackfillCycle.RunForHouseholdAsync</c>
/// does: <see cref="TenantContext"/> plus <c>SetHouseholdId</c> on every EF context the detector catalogue
/// touches (Housekeeping for dismissals, Inventory/Catalog/Recipes/Pricing for the detectors themselves).
/// It then delegates to <c>GetTidyUpPageQuery</c> — the single place that ever writes a real count into
/// the cache — so the refreshed badge can never diverge from what the Tidy Up page itself would show.
/// </para>
/// </summary>
public sealed class TidyUpBadgeRefresher(IBackgroundTaskQueue queue, ILogger<TidyUpBadgeRefresher> logger)
{
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    /// <summary>
    /// Requests a background recompute of <paramref name="householdId"/>'s badge count. A no-op if a
    /// refresh for this household is already enqueued/running (single-flight). Non-blocking beyond the
    /// queue's own bounded-capacity backpressure — never runs a detector inline on the caller's thread,
    /// and never throws: this is a best-effort side channel called from page-render code paths (the
    /// layout, the More hub), so a failure to enqueue must never fail the page itself.
    /// </summary>
    public async Task RequestRefreshAsync(HouseholdId householdId, CancellationToken ct = default)
    {
        var id = householdId.Value;
        if (!_inFlight.TryAdd(id, 0))
            return; // already in flight — the pending refresh will cover this household

        try
        {
            await queue.EnqueueAsync(async (sp, workCt) =>
            {
                try
                {
                    await RefreshOneAsync(sp, id, workCt);
                }
                finally
                {
                    // Released only once the recompute actually ran (or failed), so a fresh miss/stale
                    // read that arrives while this one is still in the queue never double-enqueues.
                    _inFlight.TryRemove(id, out _);
                }
            }, ct);
        }
        catch (Exception ex)
        {
            // Enqueue itself failed (should not happen — BackgroundTaskQueue.EnqueueAsync never throws
            // except for a null work item — but a caller-supplied ct can still cancel the await) —
            // release the guard so a later request can retry, and swallow: see the "never throws" note above.
            _inFlight.TryRemove(id, out _);
            logger.LogError(ex, "Failed to enqueue Tidy Up badge refresh for household {HouseholdId}.", id);
        }
    }

    /// <summary>
    /// Recomputes one household's badge count in a fresh, tenancy-armed scope. Isolated from the caller —
    /// exceptions are logged, never rethrown into the queue drain loop beyond what <c>QueuedHostedService</c>
    /// already catches, and never leave the household's badge entry more broken than it already was (a
    /// failed refresh simply leaves the existing stale/missing entry for the next trigger to retry).
    /// </summary>
    private static async Task RefreshOneAsync(IServiceProvider sp, Guid householdId, CancellationToken ct)
    {
        sp.GetRequiredService<TenantContext>().Set(householdId);                  // arms Postgres RLS (app.household_id GUC)
        sp.GetRequiredService<HousekeepingDbContext>().SetHouseholdId(householdId); // dismissal tombstones
        sp.GetRequiredService<InventoryDbContext>().SetHouseholdId(householdId);    // stock detectors (D1/D3/D4/D6)
        sp.GetRequiredService<CatalogDbContext>().SetHouseholdId(householdId);      // product/unit reads every detector needs
        sp.GetRequiredService<RecipesDbContext>().SetHouseholdId(householdId);      // recipe-line detectors (D2/D5/D7)
        sp.GetRequiredService<PricingDbContext>().SetHouseholdId(householdId);      // D5 price-existence check

        var query = sp.GetRequiredService<GetTidyUpPageQuery>();
        await query.ExecuteAsync(ct); // the only writer of a fresh count (badgeCache.SetAsync as a side effect)
    }
}
