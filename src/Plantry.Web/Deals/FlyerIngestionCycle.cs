using Plantry.Catalog.Infrastructure;
using Plantry.Deals.Application;
using Plantry.Deals.Infrastructure;
using Plantry.Identity.Domain;
using Plantry.Pricing.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Deals;

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
    /// <c>SetHouseholdId</c> on every context the ingest + confirm side-effects touch — Deals, Catalog
    /// (stores/products/units), and Pricing (the deal-sourced observation). Getting this wrong is a
    /// cross-household leak; getting it half-right is a silent no-op — hence all of them, every household.
    /// </summary>
    public async Task RunForHouseholdAsync(HouseholdId household, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var id = household.Value;
        sp.GetRequiredService<TenantContext>().Set(id);          // arms Postgres RLS (app.household_id GUC)
        sp.GetRequiredService<DealsDbContext>().SetHouseholdId(id);    // Deals EF query filter
        sp.GetRequiredService<CatalogDbContext>().SetHouseholdId(id);  // Catalog: stores, products, units
        sp.GetRequiredService<PricingDbContext>().SetHouseholdId(id);  // Pricing: deal-sourced observation

        await sp.GetRequiredService<IngestFlyer>().RunAsync(ct);
    }
}
