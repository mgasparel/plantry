using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Intake.Application;

/// <summary>
/// Confirms an <see cref="ImportLine"/> against an <em>existing</em> Catalog product, carrying the
/// user-resolved purchase fields (quantity / unit / location / expiry / price). Orchestrates
/// <see cref="ImportLine.Confirm"/> — the line-status invariants (cannot re-confirm a dismissed or
/// committed line) live in the domain and surface here as the command's failure. Edits are only
/// permitted while the session is still <c>Ready</c>; a committed or discarded session is closed.
/// </summary>
public sealed class ResolveLineCommand(
    ImportSessionId sessionId,
    ImportLineId lineId,
    Guid productId,
    Guid? skuId,
    decimal quantity,
    Guid unitId,
    Guid locationId,
    DateOnly? expiryDate,
    decimal? price,
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

        var resolve = line.Confirm(productId, skuId, quantity, unitId, locationId, expiryDate, price);
        if (resolve.IsFailure)
            return resolve.Error;

        await sessions.SaveChangesAsync(ct);
        return Result.Success();
    }
}
