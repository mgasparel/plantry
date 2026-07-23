using Plantry.Identity.Domain;
using Plantry.SharedKernel;

namespace Plantry.Web.Housekeeping;

/// <summary>
/// Startup warmup for the T6 nav badge (plantry-h0qq): on every process start, enumerates every
/// household (unarmed cross-tenant read — the <c>identity.households</c> RLS pre-auth carve-out, same
/// precedent as <c>RecipeConversionBackfillCycle</c>/<c>FlyerIngestionCycle</c>) and requests a
/// single-flight background refresh (<see cref="TidyUpBadgeRefresher"/>) for each, so the badge is
/// populated before any user visits Tidy Up — no page render ever needs to be the one that first
/// triggers a detector run after a restart.
/// <para>
/// Homelab household count is tiny (a handful); at SaaS scale this unbounded per-household fan-out would
/// need batching/throttling to avoid a startup thundering herd against the background queue — not a
/// concern at the current single-household-cluster scale.
/// </para>
/// </summary>
public sealed class TidyUpBadgeWarmup(
    IServiceScopeFactory scopeFactory, TidyUpBadgeRefresher refresher, ILogger<TidyUpBadgeWarmup> logger)
{
    /// <summary>
    /// Requests a refresh for every household. Never throws — a failure here must not affect app startup;
    /// the miss/stale-triggered refresher already covers any household this sweep fails to warm.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            IReadOnlyList<HouseholdId> households;
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                // No TenantContext armed → app.household_id unset → the identity.households RLS policy
                // exposes all rows (the pre-auth carve-out) — the sole cross-tenant read in this sweep.
                var repo = scope.ServiceProvider.GetRequiredService<IHouseholdRepository>();
                households = await repo.ListAllIdsAsync(ct);
            }

            logger.LogInformation(
                "Tidy Up badge warmup requesting a refresh for {HouseholdCount} household(s).", households.Count);

            foreach (var household in households)
            {
                ct.ThrowIfCancellationRequested();
                await refresher.RequestRefreshAsync(household, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown mid-sweep — not a failure. Caught here (rather than left to propagate) because
            // this method is invoked fire-and-forget from the ApplicationStarted hook; nothing observes an
            // unhandled fault on that task.
            logger.LogInformation("Tidy Up badge warmup cancelled (host shutting down).");
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Tidy Up badge warmup failed; badges will populate lazily via miss-triggered refresh instead.");
        }
    }
}
