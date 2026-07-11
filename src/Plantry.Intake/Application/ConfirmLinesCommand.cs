using Microsoft.Extensions.Logging;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Intake.Application;

/// <summary>
/// Bulk-confirms a set of still-Pending <see cref="ImportLine"/>s from their server-side prefill values —
/// the server enabler for the deck-flow review checklist (plantry-kr9h). The client sends ONLY line ids;
/// the AI-suggested values never round-trip through the browser (Gate 5), exactly as the commit-time
/// auto-confirm pre-pass does (<see cref="CommitSessionCommand"/>). Every id must qualify: the line exists
/// in the session, is <see cref="LineStatus.Pending"/>, was <see cref="SuggestedConfidence.High"/>, and its
/// re-derived <see cref="ReviewPrefill"/> chain is COMPLETE — an existing product, quantity &gt; 0, and both
/// a unit and a location (the exact predicate the commit auto-confirm pass uses).
///
/// <para><b>Atomic.</b> Every id is validated BEFORE any line is mutated, so a single non-qualifying id
/// (Low/None confidence, incomplete prefill, already-Confirmed/Dismissed/Committed, or an id from another
/// session/household) fails the whole command with a descriptive error naming the offending line and
/// confirms nothing — never a partial confirm. Tenancy + session-Ready guards match
/// <see cref="RestoreLineCommand"/> / <see cref="ReopenLineCommand"/>.</para>
/// </summary>
public sealed class ConfirmLinesCommand(
    ImportSessionId sessionId,
    IReadOnlyList<ImportLineId> lineIds,
    IImportSessionRepository sessions,
    IReviewReferenceDataProvider referenceData,
    IClock clock,
    ITenantContext tenant,
    ILogger<ConfirmLinesCommand>? logger = null)
{
    public async Task<Result<IReadOnlyList<Guid>>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Error.Unauthorized;

        if (lineIds.Count == 0)
            return Error.Custom("Intake.NoLinesToConfirm", "No lines were selected to confirm.");

        var session = await sessions.FindAsync(sessionId, ct);
        if (session is null)
        {
            logger?.LogWarning("ConfirmLines failed — session {SessionId} not found.", sessionId.Value);
            return Error.NotFound;
        }
        if (session.Status != ImportStatus.Ready)
        {
            logger?.LogWarning("ConfirmLines failed — session {SessionId} is not Ready (status: {Status}).", sessionId.Value, session.Status);
            return Error.Custom("Intake.SessionNotReady", $"Cannot edit a session in status '{session.Status}'.");
        }

        // Re-derive the prefill inputs server-side (never trust the client): the household's Catalog
        // reference data → the shared prefill lookups, plus today's date for any due-date-derived expiry.
        var reference = await referenceData.GetAsync(ct);
        var lookups = ReviewPrefill.BuildLookups(reference);
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

        // ── Pass 1: validate EVERY id qualifies before mutating a single line (atomicity). ──
        var qualified = new List<(ImportLine Line, (Guid? ProductId, string? ProductName, decimal? Qty, Guid? UnitId, Guid? LocationId, decimal? Price, DateOnly? Expiry) Prefill)>(lineIds.Count);
        foreach (var lineId in lineIds)
        {
            var line = session.Lines.SingleOrDefault(l => l.Id == lineId);
            if (line is null)
            {
                logger?.LogWarning("ConfirmLines blocked — line {LineId} not found in session {SessionId}.", lineId.Value, sessionId.Value);
                return Error.Custom(
                    "Intake.LineNotInSession",
                    $"Line {lineId.Value} is not part of this session and can't be confirmed.");
            }

            var prefill = ReviewPrefill.ComputePrefill(ReviewLineView.FromDomain(line), lookups, today);

            // The exact qualification predicate CommitSessionCommand's commit-time auto-confirm uses: a
            // still-Pending line the AI was High-confident about, whose server-side prefill is complete.
            var qualifies = line.Status == LineStatus.Pending
                && line.SuggestedConfidence == SuggestedConfidence.High
                && prefill.ProductId is not null
                && prefill.Qty is > 0m
                && prefill.UnitId is not null
                && prefill.LocationId is not null;

            if (!qualifies)
            {
                logger?.LogWarning(
                    "ConfirmLines blocked — line {LineNo} in session {SessionId} does not qualify for bulk confirm.",
                    line.LineNo, sessionId.Value);
                return Error.Custom(
                    "Intake.LineNotConfirmable",
                    $"Line {line.LineNo} (\"{line.ReceiptText}\") can't be bulk-confirmed — review it first.");
            }

            qualified.Add((line, prefill));
        }

        // ── Pass 2: confirm every qualified line from its re-derived prefill values, then save once. ──
        var confirmedIds = new List<Guid>(qualified.Count);
        foreach (var (line, prefill) in qualified)
        {
            var confirm = line.Confirm(
                prefill.ProductId!.Value, line.SkuId, prefill.Qty!.Value, prefill.UnitId!.Value,
                prefill.LocationId!.Value, prefill.Expiry, prefill.Price);
            if (confirm.IsFailure)
            {
                logger?.LogWarning(
                    "ConfirmLines failed for line {LineNo} in session {SessionId}: {ErrorCode}.",
                    line.LineNo, sessionId.Value, confirm.Error.Code);
                return confirm.Error;
            }
            confirmedIds.Add(line.Id.Value);
        }

        await sessions.SaveChangesAsync(ct);
        logger?.LogInformation(
            "Import session {SessionId}: bulk-confirmed {Count} line(s) from their prefill values.",
            sessionId.Value, confirmedIds.Count);

        return Result<IReadOnlyList<Guid>>.Success(confirmedIds);
    }
}
