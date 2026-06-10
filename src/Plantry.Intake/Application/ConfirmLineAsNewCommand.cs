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

        var confirm = line.ConfirmAsNew(
            newProductName, newProductCategoryId, quantity, unitId, locationId, expiryDate, price);
        if (confirm.IsFailure)
            return confirm.Error;

        await sessions.SaveChangesAsync(ct);
        return Result.Success();
    }
}
