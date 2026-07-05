using Microsoft.Extensions.Logging;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Recipes.Application;

/// <summary>
/// Application service that retro-applies <see cref="CookConsumeLineStatus.DeferredUnitGap"/> consume
/// lines once a <c>ProductConversion</c> bridges the (product, unit-pair) they were waiting on
/// (plantry-qll2.6).
/// <para>
/// A deferred line records a cook whose decrement could not run because no conversion bridged the
/// ingredient unit to the product's stock unit. It is NOT a shortfall — the pantry was untouched — so
/// the consume is simply owed until the math arrives, whatever its provenance (qll2.4's async AI seed,
/// or a user-entered / promoted factor). This service is the convergence step: given the product(s)
/// whose conversions just changed, it re-drives every deferred line for those products through the same
/// idempotent <see cref="IInventoryConsumer"/> path a cook uses, so the FEFO lots are picked at
/// application time but the journal row still traces to the original <c>CookEvent</c> via its
/// <c>sourceRef</c> (ADR-011).
/// </para>
/// <para>
/// Wired the same way as the qll2.4 seed seam — a synchronous follow-up call from the Web/Composition
/// layer right after a conversion lands (manual Add/Promote on the product detail page, or the AI-seed
/// background trigger), NOT a cross-context event bus (ADR-014 rules out a shared outbox until
/// reconciliation from durable state is impossible). It is also called opportunistically for a cook's
/// own product set at the top of <see cref="CookRecipe"/>, so a missed or failed Composition-layer
/// trigger self-heals on the next relevant cook.
/// </para>
/// <para>
/// Per-line outcome on re-drive:
/// <list type="bullet">
/// <item>Consume returns → <see cref="CookConsumeLine.MarkApplied"/> (with any residual shortfall): the
/// conversion now bridges and a journal row was written.</item>
/// <item><see cref="DeferredUnitGapException"/> → left <see cref="CookConsumeLineStatus.DeferredUnitGap"/>:
/// the conversion that landed did not bridge THIS pair (e.g. a different unit pair), so the line keeps
/// waiting for the right factor.</item>
/// <item><see cref="InvalidOperationException"/> (no stock record now) → <see cref="CookConsumeLine.MarkShorted"/>:
/// the product's stock was fully removed since the cook, so the owed consume can never apply. Terminal —
/// consistent with <c>ReconcilePendingCooks</c>' no-stock handling.</item>
/// </list>
/// </para>
/// </summary>
public sealed class ApplyDeferredUnitGaps(
    ICookEventRepository cookEvents,
    IInventoryConsumer consumer,
    ITenantContext tenant,
    ILogger<ApplyDeferredUnitGaps> logger)
{
    /// <summary>
    /// Re-drives every <see cref="CookConsumeLineStatus.DeferredUnitGap"/> line for the current household
    /// whose product is in <paramref name="productIds"/>, transitioning each to Applied (or Shorted when
    /// the stock record is gone). Returns the number of lines that were actually applied. No-op when
    /// there is no tenant, no products, or nothing deferred for those products.
    /// </summary>
    public async Task<int> ExecuteAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null || productIds.Count == 0)
            return 0;

        var events = await cookEvents.ListWithDeferredUnitGapLinesForProductsAsync(productIds, ct);
        if (events.Count == 0)
            return 0;

        var productSet = productIds as HashSet<Guid> ?? [.. productIds];
        var appliedCount = 0;

        foreach (var cookEvent in events)
        {
            var deferredLines = cookEvent.ConsumeLines
                .Where(l => l.Status == CookConsumeLineStatus.DeferredUnitGap && productSet.Contains(l.ProductId))
                .ToList();

            if (deferredLines.Count == 0)
                continue;

            var changed = false;
            foreach (var line in deferredLines)
            {
                try
                {
                    // Re-drive via the same idempotent consume path a cook uses. The original deferred
                    // consume never wrote a journal row (its planning pass failed before any mutation), so
                    // this is a real first application — the (sourceRef, sourceLineRef) token guards only
                    // against a genuine double-apply.
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
                    changed = true;
                    appliedCount++;
                }
                catch (DeferredUnitGapException)
                {
                    // The conversion that landed does not bridge this pair — keep waiting. No status
                    // change, so this line is re-considered the next time a conversion lands for the product.
                    logger.LogInformation(
                        "Deferred unit-gap line {LineId} for cook {CookEventId} still unbridged after a conversion landed for product {ProductId}.",
                        line.Id.Value, cookEvent.Id.Value, line.ProductId);
                }
                catch (InvalidOperationException)
                {
                    // The product's stock record is gone since the cook — the owed consume can never apply.
                    logger.LogWarning(
                        "Deferred unit-gap line {LineId} for cook {CookEventId} shorted on retro-apply — product {ProductId} has no stock record.",
                        line.Id.Value, cookEvent.Id.Value, line.ProductId);
                    line.MarkShorted();
                    changed = true;
                }
            }

            if (changed)
                await cookEvents.SaveChangesAsync(ct);
        }

        if (appliedCount > 0)
            logger.LogInformation(
                "ApplyDeferredUnitGaps retro-applied {AppliedCount} deferred consume line(s).", appliedCount);

        return appliedCount;
    }
}
