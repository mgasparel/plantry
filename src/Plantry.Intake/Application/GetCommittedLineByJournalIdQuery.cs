using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Intake.Application;

/// <summary>
/// Everything a Pantry Product Detail History row needs to render (and eventually post) the "Amend"
/// action for one committed purchase line (ADR-023 §6/A11). <see cref="AmendedQuantity"/>/<see cref="AmendedAt"/>
/// are non-null once the line has been amended at least once (A3 repeats) — the sheet copy distinguishes
/// "entered as X" from "entered as X · previously fixed to Y".
/// </summary>
public sealed record AmendableLine(
    Guid ImportLineId,
    Guid SessionId,
    Guid ProductId,
    decimal Quantity,
    Guid UnitId,
    decimal? AmendedQuantity,
    DateTimeOffset? AmendedAt,
    string ReceiptText);

/// <summary>
/// Reverse-lookup read for the web layer (ADR-023 §6): given the <see cref="ImportLine.JournalId"/> value
/// stamped on the lot a Purchase journal row created, resolves the committed <see cref="ImportLine"/> that
/// produced it. This is the Intake-side half of the cross-schema composition the Pantry Product Detail
/// History grid needs (ADR-021) — the Web composition root joins this against the Inventory-side journal
/// row to decide whether a given Purchase row earns the "Amend" affordance; Intake itself never reaches
/// into Inventory's schema (Gate 2).
///
/// <para>Returns <c>Intake.NotFound</c> when no committed line matches — a lot added manually (not through
/// intake) or whose committing line was since... there is no delete path for a committed line, so this is
/// effectively "not an intake-sourced lot" (spec acceptance #8: non-intake lots offer no amend path).</para>
/// </summary>
public sealed class GetCommittedLineByJournalIdQuery(
    Guid journalId,
    IImportSessionRepository sessions,
    ITenantContext tenant)
{
    public async Task<Result<AmendableLine>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        var line = await sessions.FindCommittedLineByJournalIdAsync(HouseholdId.From(householdId), journalId, ct);
        if (line is null)
            return Error.NotFound;

        var productId = line.ProductId ?? line.CreatedProductId;
        if (productId is null || line.Quantity is null || line.UnitId is null)
            return Error.Custom("Intake.LineIncomplete", "This line is missing required fields to amend.");

        return new AmendableLine(
            line.Id.Value,
            line.SessionId.Value,
            productId.Value,
            line.Quantity.Value,
            line.UnitId.Value,
            line.AmendedQuantity,
            line.AmendedAt,
            line.ReceiptText);
    }
}
