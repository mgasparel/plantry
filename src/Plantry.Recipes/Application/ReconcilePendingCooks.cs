using Microsoft.Extensions.Logging;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Recipes.Application;

/// <summary>
/// Application service that self-heals partial cooks by re-driving any
/// <see cref="CookConsumeLineStatus.Pending"/> consume lines left by an interrupted
/// <see cref="CookRecipe"/> execution (plantry-292c).
/// <para>
/// A line remains Pending when the process crashed or the request was cancelled after the
/// anchor commit (CookEvent + Pending lines) but before that line's
/// <see cref="IInventoryConsumer.ConsumeAsync"/> completed and was persisted as Applied or
/// Shorted. Re-driving is safe because every consume is idempotent via its
/// <c>sourceLineRef</c> token (292a): if the consume already ran and committed a journal row,
/// re-driving is a no-op; if it never ran, the re-drive deducts whatever stock is available
/// at reconciliation time (C8/R9 preserved).
/// </para>
/// <para>
/// Reconciliation does NOT re-drive <see cref="CookConsumeLineStatus.Shorted"/> lines —
/// those had a fully failed consume (no stock record at all). Re-driving them would not
/// produce a different outcome without stock being added first.
/// </para>
/// <para>
/// Two entry points:
/// <list type="bullet">
/// <item>Opportunistic: called at <see cref="CookRecipe"/> entry, sweeping the household's
/// Pending lines before the new cook runs (so stale Pending lines are resolved as soon as
/// the user next interacts with the cook flow).</item>
/// <item>On-demand: exposed as a protected POST endpoint (<c>/Recipes/ReconcilePending</c>)
/// for manual triggering or automation (no background poller — ADR-010).</item>
/// </list>
/// </para>
/// </summary>
public sealed class ReconcilePendingCooks(
    ICookEventRepository cookEvents,
    IInventoryConsumer consumer,
    IInventoryProducer producer,
    ITenantContext tenant,
    ILogger<ReconcilePendingCooks> logger)
{
    /// <summary>
    /// Re-drives all Pending consume lines for the current household, transitioning each to
    /// Applied or Shorted, and returns the number of lines reconciled.
    /// </summary>
    /// <returns>
    /// The count of <see cref="CookConsumeLine"/> rows whose status changed during this
    /// reconciliation pass. Zero when there is nothing to do.
    /// </returns>
    public async Task<ReconcileResult> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return new ReconcileResult(0);

        // Load all CookEvents that still have at least one Pending line.
        // ConsumeLines are eagerly loaded; the EF query filter applies household isolation.
        var events = await cookEvents.ListWithPendingLinesAsync(ct);
        if (events.Count == 0)
            return new ReconcileResult(0);

        logger.LogInformation(
            "ReconcilePendingCooks found {CookEventCount} cook event(s) with pending lines.",
            events.Count);

        var reconciledCount = 0;

        foreach (var cookEvent in events)
        {
            var pendingLines = cookEvent.ConsumeLines
                .Where(l => l.Status == CookConsumeLineStatus.Pending)
                .ToList();

            var pendingProduceLines = cookEvent.ProduceLines
                .Where(l => l.Status == CookProduceLineStatus.Pending)
                .ToList();

            if (pendingLines.Count == 0 && pendingProduceLines.Count == 0)
                continue;

            foreach (var line in pendingLines)
            {
                // Re-drive via IInventoryConsumer. Idempotent through the sourceLineRef token (292a):
                // if the consume already committed a journal row with this token, the call is a no-op.
                // If it never ran, the consume-available deducts whatever stock exists now (C8/R9).
                try
                {
                    var result = await consumer.ConsumeAsync(
                        line.ProductId,
                        line.Quantity,
                        line.UnitId,
                        ConsumeReason.Recipe,
                        cookEvent.Id.Value,
                        cookEvent.CookedBy,
                        sourceLineRef: line.Id.Value,
                        ct);

                    line.MarkApplied(result.ShortfallAmount);
                }
                catch (DeferredUnitGapException)
                {
                    // No conversion bridges the ingredient unit to the stock unit (plantry-qll2.6). This is
                    // NOT a shortfall — the pantry is untouched — so record it as a deferred unit gap, to be
                    // retro-applied when a conversion lands, rather than Shorted. Caught before the no-stock
                    // catch below so the discrimination is preserved through reconciliation too.
                    logger.LogInformation(
                        "Reconcile line {LineId} for cook {CookEventId} deferred — no conversion bridges the unit gap for product {ProductId}.",
                        line.Id.Value, cookEvent.Id.Value, line.ProductId);
                    line.MarkDeferredUnitGap();
                }
                catch (InvalidOperationException)
                {
                    // Product has no stock record — fully short. Mark Shorted so this line
                    // is not re-attempted on the next reconciliation pass.
                    logger.LogWarning(
                        "Reconcile line {LineId} for cook {CookEventId} shorted — product {ProductId} has no stock record.",
                        line.Id.Value, cookEvent.Id.Value, line.ProductId);
                    line.MarkShorted();
                }

                reconciledCount++;
            }

            // Re-drive Pending yield-on-cook produce lines (plantry-854a). Idempotent through the
            // sourceLineRef token exactly like consumes: if the produce already committed a journal row,
            // the re-drive is a no-op; if it never ran, it adds the yield lot now. A produce that cannot be
            // recorded is marked Failed (terminal) so it is not re-attempted on the next pass.
            foreach (var produceLine in pendingProduceLines)
            {
                try
                {
                    await producer.ProduceAsync(
                        produceLine.ProductId,
                        produceLine.Quantity,
                        produceLine.UnitId,
                        produceLine.ExpiryDate,
                        ProduceReason.Recipe,
                        cookEvent.Id.Value,
                        cookEvent.CookedBy,
                        sourceLineRef: produceLine.Id.Value,
                        ct);

                    produceLine.MarkApplied();
                }
                catch (InvalidOperationException)
                {
                    logger.LogWarning(
                        "Reconcile produce line {LineId} for cook {CookEventId} failed — product {ProductId} could not be stored.",
                        produceLine.Id.Value, cookEvent.Id.Value, produceLine.ProductId);
                    produceLine.MarkFailed();
                }

                reconciledCount++;
            }

            // Persist the status transitions for this CookEvent in one SaveChanges call.
            await cookEvents.SaveChangesAsync(ct);
        }

        if (reconciledCount > 0)
            logger.LogInformation(
                "ReconcilePendingCooks resolved {ReconciledCount} pending line(s).", reconciledCount);

        return new ReconcileResult(reconciledCount);
    }
}

/// <summary>
/// The result of a <see cref="ReconcilePendingCooks.ExecuteAsync"/> call.
/// </summary>
/// <param name="ReconciledLineCount">
/// The number of <see cref="CookConsumeLine"/> rows that were transitioned from Pending to
/// Applied or Shorted during this reconciliation pass. Zero when there was nothing to do.
/// </param>
public sealed record ReconcileResult(int ReconciledLineCount)
{
    public bool HadWork => ReconciledLineCount > 0;
}
