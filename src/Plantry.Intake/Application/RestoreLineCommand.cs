using Microsoft.Extensions.Logging;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Intake.Application;

/// <summary>
/// Restores a dismissed <see cref="ImportLine"/> back to Pending (the SPEC §2e "Add anyway" action), so the
/// user can resolve it again. Orchestrates <see cref="ImportLine.Restore"/> — the invariant that only a
/// dismissed line can be restored lives in the domain. Edits are only permitted while the session is still
/// <c>Ready</c>. Sibling of the other line-resolution commands (plantry-kuv).
/// </summary>
public sealed class RestoreLineCommand(
    ImportSessionId sessionId,
    ImportLineId lineId,
    IImportSessionRepository sessions,
    ITenantContext tenant,
    ILogger<RestoreLineCommand>? logger = null)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Error.Unauthorized;

        var session = await sessions.FindAsync(sessionId, ct);
        if (session is null)
        {
            logger?.LogWarning("RestoreLine failed — session {SessionId} not found.", sessionId.Value);
            return Error.NotFound;
        }
        if (session.Status != ImportStatus.Ready)
        {
            logger?.LogWarning("RestoreLine failed — session {SessionId} is not Ready (status: {Status}).", sessionId.Value, session.Status);
            return Error.Custom("Intake.SessionNotReady", $"Cannot edit a session in status '{session.Status}'.");
        }

        var line = session.Lines.SingleOrDefault(l => l.Id == lineId);
        if (line is null)
        {
            logger?.LogWarning("RestoreLine failed — line {LineId} not found in session {SessionId}.", lineId.Value, sessionId.Value);
            return Error.NotFound;
        }

        var restore = line.Restore();
        if (restore.IsFailure)
        {
            logger?.LogWarning("RestoreLine failed for line {LineId} in session {SessionId}: {ErrorCode}.", lineId.Value, sessionId.Value, restore.Error.Code);
            return restore.Error;
        }

        await sessions.SaveChangesAsync(ct);
        logger?.LogInformation("Import line {LineId} restored for session {SessionId}.", lineId.Value, sessionId.Value);
        return Result.Success();
    }
}
