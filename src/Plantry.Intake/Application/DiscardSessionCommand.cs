using Microsoft.Extensions.Logging;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Intake.Application;

/// <summary>
/// Discards an entire <see cref="ImportSession"/> (SPEC §2e) — the user abandons the receipt without
/// committing any line. Orchestrates <see cref="ImportSession.Discard"/>; the invariant that a committed
/// session cannot be discarded lives in the domain and surfaces here as the command's failure.
/// </summary>
public sealed class DiscardSessionCommand(
    ImportSessionId sessionId,
    IImportSessionRepository sessions,
    ITenantContext tenant,
    ILogger<DiscardSessionCommand>? logger = null)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Error.Unauthorized;

        var session = await sessions.FindAsync(sessionId, ct);
        if (session is null)
            return Error.NotFound;

        var discard = session.Discard();
        if (discard.IsFailure)
        {
            logger?.LogWarning(
                "DiscardSession failed for session {SessionId}: {ErrorCode}.",
                sessionId.Value, discard.Error.Code);
            return discard.Error;
        }

        await sessions.SaveChangesAsync(ct);
        logger?.LogInformation("Import session {SessionId} discarded.", sessionId.Value);
        return Result.Success();
    }
}
