using Microsoft.Extensions.Logging;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Intake.Application;

/// <summary>
/// Dismisses an <see cref="ImportLine"/> so it is skipped at commit (SPEC §2e). Orchestrates
/// <see cref="ImportLine.Dismiss"/> — the invariant that an already-committed line cannot be dismissed
/// lives in the domain. Edits are only permitted while the session is still <c>Ready</c>.
/// </summary>
public sealed class DismissLineCommand(
    ImportSessionId sessionId,
    ImportLineId lineId,
    IImportSessionRepository sessions,
    ITenantContext tenant,
    ILogger<DismissLineCommand>? logger = null)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Error.Unauthorized;

        var session = await sessions.FindAsync(sessionId, ct);
        if (session is null)
        {
            logger?.LogWarning("DismissLine failed — session {SessionId} not found.", sessionId.Value);
            return Error.NotFound;
        }
        if (session.Status != ImportStatus.Ready)
        {
            logger?.LogWarning("DismissLine failed — session {SessionId} is not Ready (status: {Status}).", sessionId.Value, session.Status);
            return Error.Custom("Intake.SessionNotReady", $"Cannot edit a session in status '{session.Status}'.");
        }

        var line = session.Lines.SingleOrDefault(l => l.Id == lineId);
        if (line is null)
        {
            logger?.LogWarning("DismissLine failed — line {LineId} not found in session {SessionId}.", lineId.Value, sessionId.Value);
            return Error.NotFound;
        }

        var dismiss = line.Dismiss();
        if (dismiss.IsFailure)
        {
            logger?.LogWarning("DismissLine failed for line {LineId} in session {SessionId}: {ErrorCode}.", lineId.Value, sessionId.Value, dismiss.Error.Code);
            return dismiss.Error;
        }

        await sessions.SaveChangesAsync(ct);
        logger?.LogInformation("Import line {LineId} dismissed for session {SessionId}.", lineId.Value, sessionId.Value);
        return Result.Success();
    }
}
