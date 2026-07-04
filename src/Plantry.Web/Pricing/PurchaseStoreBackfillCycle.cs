using Plantry.Catalog.Infrastructure;
using Plantry.Identity.Domain;
using Plantry.Pricing.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Pricing;

/// <summary>
/// Drives the one-time DM-16 store-id backfill (bead part D) across every household, reproducing
/// <c>RlsMiddleware</c>'s tenancy arming with <b>no HTTP request</b> — the same cross-tenant structure as
/// <c>FlyerIngestionCycle</c>. Deliberately <b>not</b> a <see cref="BackgroundService"/> and it never runs
/// at boot: it is a one-shot reconciliation, exposed only through a dev-only manual endpoint
/// (mirroring <c>/Dev/Deals/PullNow</c>). Idempotent, so re-triggering it is safe.
/// <para>
/// <b>Cross-tenant enumeration (the one place scoping is stepped outside).</b> The first scope arms
/// <b>no</b> tenant, so <c>app.household_id</c> is unset and <see cref="IHouseholdRepository.ListAllIdsAsync"/>
/// (which also ignores the EF filter) returns every household. Each household is then processed in its
/// <b>own</b> fresh scope with tenancy fully armed, so one household's rows can never leak into another's.
/// </para>
/// </summary>
public sealed class PurchaseStoreBackfillCycle(IServiceScopeFactory scopeFactory, ILogger<PurchaseStoreBackfillCycle> logger)
{
    /// <summary>Sweeps every household, isolating a per-household failure so one bad household never aborts the cycle.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        IReadOnlyList<HouseholdId> households;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            // No TenantContext armed → app.household_id unset → the identity.households RLS policy exposes
            // all rows (the pre-auth carve-out). This is the sole cross-tenant read in the sweep.
            var repo = scope.ServiceProvider.GetRequiredService<IHouseholdRepository>();
            households = await repo.ListAllIdsAsync(ct);
        }

        logger.LogInformation("Purchase-store backfill sweep starting for {HouseholdCount} household(s).", households.Count);

        foreach (var household in households)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await RunForHouseholdAsync(household, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Purchase-store backfill failed for household {HouseholdId}; continuing to the next.", household.Value);
            }
        }
    }

    /// <summary>
    /// Processes one household in a fresh scope with tenancy armed exactly as <c>RlsMiddleware</c> does:
    /// <see cref="TenantContext"/> (arms the Postgres GUC via the connection interceptor) plus
    /// <c>SetHouseholdId</c> on every context the backfill touches — Catalog (store find-or-create) and
    /// Pricing (the observation being enriched). Both must be armed or the sweep is a cross-household leak
    /// (or a silent no-op) — hence both, every household.
    /// </summary>
    public async Task RunForHouseholdAsync(HouseholdId household, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var id = household.Value;
        sp.GetRequiredService<TenantContext>().Set(id);              // arms Postgres RLS (app.household_id GUC)
        sp.GetRequiredService<CatalogDbContext>().SetHouseholdId(id); // Catalog: store find-or-create
        sp.GetRequiredService<PricingDbContext>().SetHouseholdId(id); // Pricing: the observation being enriched

        await sp.GetRequiredService<PurchaseStoreBackfill>().RunAsync(ct);
    }
}
