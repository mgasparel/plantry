using Microsoft.Extensions.Logging;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Recipes.Application;

/// <summary>
/// Application service that voids pending <see cref="CookConsumeLineStatus.DeferredUnitGap"/> lines for a
/// product when an absolute observation captures its true stock level directly (plantry-qll2.6).
/// <para>
/// This is the load-bearing safety rule of deferred consume. A deferred line is a <em>relative</em>
/// delta the pantry still owes; a Take Stock count / manual absolute adjustment is an <em>absolute</em>
/// observation of what is actually on the shelf. Once reality has been observed, retro-applying the
/// deferred delta afterwards would double-count — the count already reflects it. So an absolute
/// observation supersedes any outstanding deferred consume for that product: each such line is moved to
/// the terminal <see cref="CookConsumeLineStatus.SupersededByCount"/> state and is never retro-applied,
/// even if the conversion it was waiting on lands later.
/// </para>
/// <para>
/// Wired the same way as <see cref="ApplyDeferredUnitGaps"/> — a synchronous follow-up call from the
/// Web/Composition layer right after the absolute-adjustment command succeeds (Take Stock count).
/// </para>
/// </summary>
public sealed class VoidDeferredUnitGapLines(
    ICookEventRepository cookEvents,
    ITenantContext tenant,
    ILogger<VoidDeferredUnitGapLines> logger)
{
    /// <summary>
    /// Voids every outstanding <see cref="CookConsumeLineStatus.DeferredUnitGap"/> line for
    /// <paramref name="productId"/> in the current household, transitioning each to
    /// <see cref="CookConsumeLineStatus.SupersededByCount"/>. Returns the number of lines voided. No-op
    /// when there is no tenant or nothing deferred for the product.
    /// </summary>
    public async Task<int> ExecuteAsync(Guid productId, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return 0;

        var events = await cookEvents.ListWithDeferredUnitGapLinesForProductsAsync([productId], ct);
        if (events.Count == 0)
            return 0;

        var voidedCount = 0;
        foreach (var cookEvent in events)
        {
            var deferredLines = cookEvent.ConsumeLines
                .Where(l => l.Status == CookConsumeLineStatus.DeferredUnitGap && l.ProductId == productId)
                .ToList();

            if (deferredLines.Count == 0)
                continue;

            foreach (var line in deferredLines)
            {
                line.MarkSupersededByCount();
                voidedCount++;
            }

            await cookEvents.SaveChangesAsync(ct);
        }

        if (voidedCount > 0)
            logger.LogInformation(
                "VoidDeferredUnitGapLines superseded {VoidedCount} deferred consume line(s) for product {ProductId} after an absolute count.",
                voidedCount, productId);

        return voidedCount;
    }
}
