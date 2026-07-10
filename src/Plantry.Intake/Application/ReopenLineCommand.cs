using Microsoft.Extensions.Logging;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Intake.Application;

/// <summary>
/// Reopens a confirmed <see cref="ImportLine"/> back to Pending so the user can resolve it again — the
/// undo-of-a-resolve and the "Wrong product — review again" rematch in the exceptions-first review flow
/// (plantry-v0wl). Orchestrates <see cref="ImportLine.Reopen"/> — the invariant that only a confirmed line
/// can be reopened (and the clearing of user-resolved fields) lives in the domain. Edits are only permitted
/// while the session is still <c>Ready</c>. Sibling of <see cref="RestoreLineCommand"/>.
/// </summary>
public sealed class ReopenLineCommand(
    ImportSessionId sessionId,
    ImportLineId lineId,
    IImportSessionRepository sessions,
    ITenantContext tenant,
    ILogger<ReopenLineCommand>? logger = null)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Error.Unauthorized;

        var session = await sessions.FindAsync(sessionId, ct);
        if (session is null)
        {
            logger?.LogWarning("ReopenLine failed — session {SessionId} not found.", sessionId.Value);
            return Error.NotFound;
        }
        if (session.Status != ImportStatus.Ready)
        {
            logger?.LogWarning("ReopenLine failed — session {SessionId} is not Ready (status: {Status}).", sessionId.Value, session.Status);
            return Error.Custom("Intake.SessionNotReady", $"Cannot edit a session in status '{session.Status}'.");
        }

        var line = session.Lines.SingleOrDefault(l => l.Id == lineId);
        if (line is null)
        {
            logger?.LogWarning("ReopenLine failed — line {LineId} not found in session {SessionId}.", lineId.Value, sessionId.Value);
            return Error.NotFound;
        }

        var reopen = line.Reopen();
        if (reopen.IsFailure)
        {
            logger?.LogWarning("ReopenLine failed for line {LineId} in session {SessionId}: {ErrorCode}.", lineId.Value, sessionId.Value, reopen.Error.Code);
            return reopen.Error;
        }

        await sessions.SaveChangesAsync(ct);
        logger?.LogInformation("Import line {LineId} reopened for session {SessionId}.", lineId.Value, sessionId.Value);
        return Result.Success();
    }
}
