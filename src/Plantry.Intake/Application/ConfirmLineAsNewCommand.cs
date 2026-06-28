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
    ILogger<ConfirmLineAsNewCommand>? logger = null)
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

        var confirm = line.ConfirmAsNew(
            newProductName, newProductCategoryId, quantity, unitId, locationId, expiryDate, price);
        if (confirm.IsFailure)
        {
            logger?.LogWarning("ConfirmLineAsNew failed for line {LineId} in session {SessionId}: {ErrorCode}.", lineId.Value, sessionId.Value, confirm.Error.Code);
            return confirm.Error;
        }

        await sessions.SaveChangesAsync(ct);
        logger?.LogInformation("Import line {LineId} confirmed as new product for session {SessionId}.", lineId.Value, sessionId.Value);
        return Result.Success();
    }
}
