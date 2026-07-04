using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.Pricing.Domain;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Pricing;

/// <summary>
/// Per-household unit of work for the DM-16 store-id backfill (bead part D): the enrichment counterpart to
/// the intake commit's live store stamping. Runs inside an already-armed tenant scope (see
/// <see cref="PurchaseStoreBackfillCycle"/>) and, for each historical purchase <c>price_observation</c>
/// that predates the write-path change (<c>source = Purchase</c>, <c>store_id IS NULL</c>, non-blank
/// <c>merchant_text</c>), resolves its merchant to a <c>catalog.store</c> identity and binds it via
/// <see cref="PriceObservation.ResolveStore"/>.
/// <para>
/// Merchant → store resolution reuses Catalog's <see cref="EnsureStoreByNameCommand"/> — the same
/// find-or-create the intake commit path uses (<c>CommitSessionCommand</c> via
/// <c>EnsurePurchaseStoreAdapter</c>) — so a purchase and a deal for the same merchant share one store
/// identity. Pricing never touches <c>CatalogDbContext</c> directly: this class lives in Plantry.Web (the
/// composition root) and drives the Catalog command over its own <see cref="IStoreRepository"/> (ADR-010).
/// </para>
/// <para>
/// <b>Idempotent + re-runnable.</b> Eligibility already excludes resolved rows, and
/// <see cref="PriceObservation.ResolveStore"/> is a no-op once a store is set — so a second run resolves
/// nothing. Each resolved observation is saved on its own so a mid-sweep failure leaves prior work
/// persisted (one aggregate per transaction); a store that could not be resolved is logged and skipped
/// without aborting the household.
/// </para>
/// </summary>
public sealed class PurchaseStoreBackfill(
    IPriceObservationRepository observations,
    IStoreRepository stores,
    ITenantContext tenant,
    IClock clock,
    ILogger<PurchaseStoreBackfill> logger)
{
    /// <summary>Resolves and stamps every eligible observation for the currently-armed household; returns
    /// the number of observations newly bound to a store.</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var pending = await observations.ListPurchasesAwaitingStoreAsync(ct);
        if (pending.Count == 0)
            return 0;

        var resolved = 0;
        foreach (var observation in pending)
        {
            ct.ThrowIfCancellationRequested();

            // merchant_text is guaranteed non-blank by the query; the command guards it again.
            var storeResult = await new EnsureStoreByNameCommand(observation.MerchantText!, stores, tenant, clock)
                .ExecuteAsync(ct);
            if (storeResult.IsFailure)
            {
                logger.LogWarning(
                    "Purchase-store backfill could not resolve a store for observation {ObservationId}: {ErrorCode}.",
                    observation.Id.Value, storeResult.Error.Code);
                continue;
            }

            if (observation.ResolveStore(storeResult.Value.Value))
            {
                await observations.SaveChangesAsync(ct);
                resolved++;
            }
        }

        logger.LogInformation(
            "Purchase-store backfill resolved {ResolvedCount} of {PendingCount} pending observation(s).",
            resolved, pending.Count);
        return resolved;
    }
}
