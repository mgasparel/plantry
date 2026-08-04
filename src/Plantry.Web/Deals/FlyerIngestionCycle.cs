using Plantry.Pantry.Infrastructure;
using Plantry.Market.Application;
using Plantry.Market.Domain;
using Plantry.Market.Infrastructure;
using Plantry.Identity.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Deals;

/// <summary>
/// The subset of <see cref="FlyerIngestionCycle"/> that <see cref="FlyerIngestionWorker"/> depends on —
/// extracted purely as a test seam so the worker's boot orchestration (skip-when-disabled,
/// catch-and-continue on a throwing cycle) can be exercised against a fake without a live DI container.
/// <see cref="FlyerIngestionCycle"/> is still registered and resolved as itself everywhere else
/// (e.g. the dev-only manual endpoint in Program.cs) — this interface changes no runtime behavior.
/// </summary>
public interface IFlyerIngestionCycle
{
    Task RunAsync(CancellationToken ct = default);

    Task<DateTimeOffset?> GetLastPullAcrossHouseholdsAsync(CancellationToken ct = default);
}

/// <summary>
/// Drives one full ingestion sweep (P5-6 / DJ2), reproducing <c>RlsMiddleware</c>'s tenancy arming with
/// <b>no HTTP request</b> — the security-critical heart of the slice. Lives in Plantry.Web (the
/// composition root) because it must open per-household DI scopes and arm every bounded-context DbContext
/// the pipeline touches; the per-household work itself is the context-owned <see cref="IngestFlyer"/>.
/// <para>
/// <b>Cross-tenant enumeration (the one place scoping is stepped outside).</b> The first scope arms
/// <b>no</b> tenant, so <c>app.household_id</c> is unset and <see cref="IHouseholdRepository.ListAllIdsAsync"/>
/// (which also ignores the EF filter) returns every household. Each household is then processed in its
/// <b>own</b> fresh scope with tenancy fully armed, so household A's pull can never read or write
/// household B's rows.
/// </para>
/// </summary>
public sealed class FlyerIngestionCycle(IServiceScopeFactory scopeFactory, ILogger<FlyerIngestionCycle> logger)
    : IFlyerIngestionCycle
{
    /// <summary>Sweeps every household, isolating a per-household failure so one bad household never aborts the cycle.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        IReadOnlyList<HouseholdId> households;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            // No TenantContext armed → app.household_id unset → the identity.households RLS policy exposes
            // all rows (the pre-auth carve-out). This is the sole cross-tenant read in the pipeline.
            var repo = scope.ServiceProvider.GetRequiredService<IHouseholdRepository>();
            households = await repo.ListAllIdsAsync(ct);
        }

        logger.LogInformation("Flyer ingestion sweep starting for {HouseholdCount} household(s).", households.Count);

        foreach (var household in households)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await RunForHouseholdAsync(household, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Flyer ingestion failed for household {HouseholdId}; continuing to the next.", household.Value);
            }
        }
    }

    /// <summary>
    /// Processes one household in a fresh scope with tenancy armed exactly as <c>RlsMiddleware</c> does:
    /// <see cref="TenantContext"/> (arms the Postgres GUC via the connection interceptor) plus
    /// <c>SetHouseholdId</c> on every context the ingest + confirm side-effects touch — Market (deals +
    /// the deal-sourced pricing observation, one context since plantry-g3da.7) and Catalog
    /// (stores/products/units). Getting this wrong is a cross-household leak; getting it half-right is a
    /// silent no-op — hence all of them, every household.
    /// </summary>
    public async Task RunForHouseholdAsync(HouseholdId household, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var id = household.Value;
        sp.GetRequiredService<TenantContext>().Set(id);          // arms Postgres RLS (app.household_id GUC)
        sp.GetRequiredService<MarketDbContext>().SetHouseholdId(id);   // Market EF query filter (pricing + deals)
        sp.GetRequiredService<CatalogDbContext>().SetHouseholdId(id);  // Catalog: stores, products, units

        await sp.GetRequiredService<IngestFlyer>().RunAsync(ct);
    }

    /// <summary>
    /// plantry-rb36: the boot due-check's cross-tenant read — the latest successful pull recorded by
    /// <b>any</b> household, or <c>null</c> if none ever has. Opens its own unarmed scope, exactly like the
    /// household enumeration in <see cref="RunAsync"/>: no <see cref="TenantContext"/> is set, so
    /// <c>deals.store_subscription</c>'s RLS policy (extended by the
    /// <c>AllowCrossHouseholdStoreSubscriptionRead</c> migration) exposes every household's rows to this one
    /// query. <see cref="FlyerIngestionWorker"/> uses the result to decide whether a sweep is already due at
    /// boot, or how long to wait for the next one.
    /// </summary>
    public async Task<DateTimeOffset?> GetLastPullAcrossHouseholdsAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IStoreSubscriptionRepository>();
        return await repo.GetLastPulledAtAcrossHouseholdsAsync(ct);
    }
}
