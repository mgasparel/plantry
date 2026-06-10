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
    ITenantContext tenant)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Error.Unauthorized;

        var session = await sessions.FindAsync(sessionId, ct);
        if (session is null)
            return Error.NotFound;
        if (session.Status != ImportStatus.Ready)
            return Error.Custom("Intake.SessionNotReady", $"Cannot edit a session in status '{session.Status}'.");

        var line = session.Lines.SingleOrDefault(l => l.Id == lineId);
        if (line is null)
            return Error.NotFound;

        var dismiss = line.Dismiss();
        if (dismiss.IsFailure)
            return dismiss.Error;

        await sessions.SaveChangesAsync(ct);
        return Result.Success();
    }
}
