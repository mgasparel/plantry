using Microsoft.Extensions.Logging;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Intake.Application;

/// <summary>
/// Confirms an <see cref="ImportLine"/> against a brand-new product (the SPEC §2d unmatched create/link
/// path): the product does not exist yet and is created at commit time (ADR-010), so no orphan product is
/// left behind if the session is never committed. Orchestrates <see cref="ImportLine.ConfirmAsNew"/> —
/// the name/status invariants live in the domain. Edits are only permitted while the session is still
/// <c>Ready</c>.
/// </summary>
public sealed class ConfirmLineAsNewCommand(
    ImportSessionId sessionId,
    ImportLineId lineId,
    string newProductName,
    Guid newProductCategoryId,
    decimal quantity,
    Guid unitId,
    Guid locationId,
    DateOnly? expiryDate,
    decimal? price,
    IImportSessionRepository sessions,
    ITenantContext tenant,
    ILogger<ConfirmLineAsNewCommand>? logger = null,
    Guid? stagedProductId = null)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Error.Unauthorized;

        var session = await sessions.FindAsync(sessionId, ct);
        if (session is null)
        {
            logger?.LogWarning("ConfirmLineAsNew failed — session {SessionId} not found.", sessionId.Value);
            return Error.NotFound;
        }
        if (session.Status != ImportStatus.Ready)
        {
            logger?.LogWarning("ConfirmLineAsNew failed — session {SessionId} is not Ready (status: {Status}).", sessionId.Value, session.Status);
            return Error.Custom("Intake.SessionNotReady", $"Cannot edit a session in status '{session.Status}'.");
        }

        var line = session.Lines.SingleOrDefault(l => l.Id == lineId);
        if (line is null)
        {
            logger?.LogWarning("ConfirmLineAsNew failed — line {LineId} not found in session {SessionId}.", lineId.Value, sessionId.Value);
            return Error.NotFound;
        }

        // Only a staged id supplied by the island is an explicit alias selection. A persisted line id is
        // never promoted into an implicit selection: a no-id retry must re-run the aggregate's full
        // normalized-name/category/default-unit conflict check.
        var staged = session.GetOrCreateStagedProduct(
            stagedProductId,
            newProductName, newProductCategoryId, unitId);
        if (staged.IsFailure)
        {
            logger?.LogWarning("ConfirmLineAsNew failed for staged product in line {LineId} of session {SessionId}: {ErrorCode}.",
                lineId.Value, sessionId.Value, staged.Error.Code);
            return staged.Error;
        }

        var confirm = line.ConfirmAsNew(
            newProductName, newProductCategoryId, quantity, unitId, locationId, expiryDate, price,
            staged.Value.Id);
        if (confirm.IsFailure)
        {
            logger?.LogWarning("ConfirmLineAsNew failed for line {LineId} in session {SessionId}: {ErrorCode}.", lineId.Value, sessionId.Value, confirm.Error.Code);
            return confirm.Error;
        }

        try
        {
            await sessions.SaveChangesAsync(ct);
        }
        catch (StagedProductNameConflictException ex)
        {
            // Another request loaded this session before us and won the normalized-name insert. The
            // repository has cleared the failed EF graph, so reload and run the domain resolver again:
            // an identical identity reuses the winner, while a category/unit conflict returns the same
            // user-facing prompt without mutating or saving this line.
            logger?.LogWarning(
                ex,
                "ConfirmLineAsNew detected a concurrent staged-product name for line {LineId} in session {SessionId}; reloading the winner.",
                lineId.Value, sessionId.Value);

            var reloaded = await sessions.FindAsync(sessionId, ct);
            if (reloaded is null)
                return Error.NotFound;

            var retry = ConfirmLine(
                reloaded, lineId, newProductName, newProductCategoryId, quantity, unitId, locationId,
                expiryDate, price, stagedProductId);
            if (retry.IsFailure)
                return retry.Error;

            try
            {
                await sessions.SaveChangesAsync(ct);
            }
            catch (StagedProductNameConflictException concurrentEx)
            {
                // A second winner is exceptionally unlikely, but never surface the provider exception
                // or persist a line whose staged alias was not resolved.
                logger?.LogWarning(
                    concurrentEx,
                    "ConfirmLineAsNew encountered a second concurrent staged-product name for line {LineId} in session {SessionId}; returning a conflict.",
                    lineId.Value, sessionId.Value);
                return Error.Custom(
                    "Intake.StagedProductNameConflict",
                    "A staged product with this name was created concurrently. " +
                    "Use Change match to select the existing staged option instead of creating another.");
            }
        }
        logger?.LogInformation("Import line {LineId} confirmed as new product for session {SessionId}.", lineId.Value, sessionId.Value);
        return Result.Success();
    }

    private static Result ConfirmLine(
        ImportSession session,
        ImportLineId lineId,
        string newProductName,
        Guid newProductCategoryId,
        decimal quantity,
        Guid unitId,
        Guid locationId,
        DateOnly? expiryDate,
        decimal? price,
        Guid? stagedProductId)
    {
        var line = session.Lines.SingleOrDefault(l => l.Id == lineId);
        if (line is null)
            return Error.NotFound;

        var staged = session.GetOrCreateStagedProduct(
            stagedProductId,
            newProductName, newProductCategoryId, unitId);
        if (staged.IsFailure)
            return staged.Error;

        return line.ConfirmAsNew(
            newProductName, newProductCategoryId, quantity, unitId, locationId, expiryDate, price,
            staged.Value.Id);
    }
}
