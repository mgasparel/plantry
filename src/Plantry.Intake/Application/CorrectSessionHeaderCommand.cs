using Microsoft.Extensions.Logging;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Intake.Application;

/// <summary>
/// Applies a user correction to a <c>Ready</c> <see cref="ImportSession"/>'s parsed receipt header
/// (plantry-yobz) — the review-time intervention point for a defensible-but-wrong AI extraction (a store
/// that came through as "Store #100616", a day/month-swapped purchase date). Orchestrates
/// <see cref="ImportSession.CorrectHeader"/>; the "only while Ready" invariant lives in the domain.
///
/// <para><b>Store resolution.</b> The user either picks an existing catalog store (an explicit
/// <paramref name="selectedStoreId"/>) or leaves/edits the merchant text for the commit-time find-or-create
/// path. When an id is supplied it is validated against the household's <em>active</em> stores (via
/// <see cref="IReviewReferenceDataProvider"/>, the same tenant-scoped pick-list the review page renders) —
/// a bogus or stale id from the browser is rejected here rather than stamping a wrong <c>store_id</c> onto a
/// price observation at commit (Gate 5: browser input is untrusted). A null id is always valid (the
/// merchant-text path).</para>
/// </summary>
public sealed class CorrectSessionHeaderCommand(
    ImportSessionId sessionId,
    string? merchantText,
    Guid? selectedStoreId,
    DateOnly? purchaseDate,
    TimeOnly? purchaseTime,
    IImportSessionRepository sessions,
    IReviewReferenceDataProvider referenceData,
    IClock clock,
    ITenantContext tenant,
    ILogger<CorrectSessionHeaderCommand>? logger = null)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Error.Unauthorized;

        var session = await sessions.FindAsync(sessionId, ct);
        if (session is null)
        {
            logger?.LogWarning("CorrectHeader failed — session {SessionId} not found.", sessionId.Value);
            return Error.NotFound;
        }
        if (session.Status != ImportStatus.Ready)
        {
            logger?.LogWarning(
                "CorrectHeader failed — session {SessionId} is not Ready (status: {Status}).",
                sessionId.Value, session.Status);
            return Error.Custom("Intake.SessionNotReady", $"Cannot edit a session in status '{session.Status}'.");
        }

        // Validate an explicitly-picked store against the household's active stores. Null = the
        // merchant-text find-or-create path (always valid); a non-null id must resolve to a real,
        // household-scoped, active store — the browser is untrusted input.
        if (selectedStoreId is { } storeId)
        {
            var reference = await referenceData.GetAsync(ct);
            if (reference.Stores.All(s => s.Id != storeId))
            {
                logger?.LogWarning(
                    "CorrectHeader rejected — store {StoreId} is not an active store for session {SessionId}.",
                    storeId, sessionId.Value);
                return Error.Custom("Intake.UnknownStore", "The selected store no longer exists — pick another or create a new one.");
            }
        }

        var correct = session.CorrectHeader(merchantText, selectedStoreId, purchaseDate, purchaseTime, clock);
        if (correct.IsFailure)
        {
            logger?.LogWarning(
                "CorrectHeader failed for session {SessionId}: {ErrorCode}.", sessionId.Value, correct.Error.Code);
            return correct.Error;
        }

        await sessions.SaveChangesAsync(ct);
        logger?.LogInformation(
            "Import session {SessionId} header corrected (store picked: {StorePicked}, date set: {DateSet}).",
            sessionId.Value, selectedStoreId is not null, purchaseDate is not null);
        return Result.Success();
    }
}
