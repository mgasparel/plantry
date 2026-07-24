using Microsoft.Extensions.Logging;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Intake.Application;

/// <summary>
/// Orchestrates a purchase-entry amendment (ADR-023, spec A8-A10, origin plantry-x3dy): the Pantry Product
/// Detail "Amend" action posts here to fix a committed receipt line's quantity after the fact, without
/// touching the append-only stock journal in place (ADR-011).
///
/// <para><b>Ordering mirrors <c>CommitSessionCommand.CommitLineAsync</c> (ADR-010 resumability):</b> amend
/// stock (Inventory) → supersede price (Pricing, only when it is actually fed by the corrected quantity,
/// A8) → <see cref="ImportLine.MarkAmended"/> → save. Each step is idempotent under retry (A10): a
/// zero-delta stock amend is a no-op success (<c>ProductStock.AmendPurchase</c>'s own delta-zero guard),
/// and the price leg's own port (<see cref="IAmendPricePort"/>, backed by
/// <c>Plantry.Web.Intake.AmendPriceAdapter</c>) resolves the TRUE live observation before superseding and
/// skips when it already matches the corrected quantity — because <c>ImportLine.PriceObservationId</c> is
/// this line's own bookkeeping copy and can go stale across a cross-context save boundary (ADR-014): a
/// prior attempt may have superseded the observation in Pricing before a later step failed to save on the
/// Intake side. <see cref="ImportLine.MarkAmended"/> advances <see cref="ImportLine.PriceObservationId"/> to
/// whatever the price leg returns (the live row, whether newly superseded or an idempotent no-op), so a
/// SUBSEQUENT amendment (spec §3's second fix) or a stale retry always chains off the correct row.</para>
///
/// <para>Unlike <c>CommitSessionCommand</c>, whose ports throw on failure because a step failing there is
/// an unexpected abort, <see cref="IAmendStockPort"/>/<see cref="IAmendPricePort"/> return
/// <see cref="Result{T}"/>: the Inventory guards this orchestration can hit
/// (<c>Inventory.AmendBelowConsumed</c>, <c>Inventory.AmendmentClosedByCorrection</c>, …) are expected,
/// user-facing validation outcomes core to the feature (spec acceptance #3/#4), not bugs — the caller (the
/// amend sheet) renders the exact error code as an in-sheet guard message.</para>
/// </summary>
public sealed class AmendCommittedLineCommand(
    Guid lineId,
    decimal correctedQuantity,
    Guid userId,
    IImportSessionRepository sessions,
    IAmendStockPort amendStock,
    IAmendPricePort amendPrice,
    IClock clock,
    ITenantContext tenant,
    ILogger<AmendCommittedLineCommand> logger)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        var household = HouseholdId.From(householdId);
        var line = await sessions.FindLineAsync(household, ImportLineId.From(lineId), ct);
        if (line is null)
            return Error.NotFound;

        // Structural eligibility (A4-i): the line must be a committed intake purchase that actually
        // produced a lot. The ledger-semantic guards (below-consumed, closed-by-correction, depleted lot —
        // A4-ii/iii/iv) are Inventory's to enforce; this orchestrator does not duplicate them, it just
        // surfaces whichever one the port returns.
        if (line.Status != LineStatus.Committed)
        {
            logger.LogWarning("AmendCommittedLine failed — line {LineId} is not committed (status: {Status}).", lineId, line.Status);
            return Error.Custom("Intake.LineNotCommitted", "Only a committed line can be amended.");
        }
        if (line.JournalId is not { } stockEntryId)
        {
            logger.LogWarning("AmendCommittedLine failed — line {LineId} has no linked purchase lot.", lineId);
            return Error.Custom("Intake.LineNotFromIntakePurchase", "This line has no linked purchase to amend.");
        }

        // A new-product line never back-fills ProductId (CommitSessionCommand.ResolveProductAsync stores
        // the created id on CreatedProductId instead, mirroring GetCommittedSessionDetailQuery's own
        // ProductId ?? CreatedProductId read).
        var productId = line.ProductId ?? line.CreatedProductId;
        if (productId is null)
        {
            logger.LogWarning("AmendCommittedLine failed — line {LineId} has no resolved product.", lineId);
            return Error.Custom("Intake.LineMissingProduct", "This line has no resolved product.");
        }

        // ── Amend stock (Inventory) — ADR-023 A2/A3/A4 ──────────────────────────────────────────────────
        var stockResult = await amendStock.AmendAsync(
            productId.Value, stockEntryId, correctedQuantity, line.Id.Value, userId, ct);
        if (stockResult.IsFailure)
        {
            logger.LogWarning(
                "AmendCommittedLine rejected by Inventory for line {LineId}. Error: {ErrorCode}.",
                lineId, stockResult.Error.Code);
            return stockResult.Error;
        }

        // ── Supersede price (Pricing) — only when the corrected quantity actually feeds the observation
        // (A8): an each-count fix on a weight-priced line (plantry-1mu) must leave the weight-denominated
        // observation untouched. The port itself absorbs the A10 retry idempotency (chains off the true
        // live row and no-ops when it already matches) — see AmendPriceAdapter. ────────────────────────────
        Guid? newPriceObservationId = null;
        if (LineCommitDecision.DecidePriceAmendment(line, correctedQuantity) is AmendPriceDecision.Amend amend)
        {
            var priceResult = await amendPrice.AmendAsync(line.PriceObservationId!.Value, amend.CorrectedQuantity, userId, ct);
            if (priceResult.IsFailure)
            {
                logger.LogWarning(
                    "AmendCommittedLine: price supersede failed for line {LineId}. Error: {ErrorCode}.",
                    lineId, priceResult.Error.Code);
                return priceResult.Error;
            }
            newPriceObservationId = priceResult.Value;
        }

        // ── Stamp the line's own record of the correction (A9) and save. PriceObservationId is advanced to
        // the new live row so a subsequent amendment (or a stale retry) chains off the right one. ──────────
        var mark = line.MarkAmended(correctedQuantity, clock.UtcNow, newPriceObservationId);
        if (mark.IsFailure)
            return mark.Error;

        await sessions.SaveChangesAsync(ct);

        logger.LogInformation(
            "Line {LineId} amended to {CorrectedQuantity} (stock delta {Delta}).",
            lineId, correctedQuantity, stockResult.Value);

        return Result.Success();
    }
}
