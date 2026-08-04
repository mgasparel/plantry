using Plantry.Intake.Domain;
using Plantry.Pantry.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Inventory;

/// <summary>
/// Composition-root adapter for <see cref="IAmendableLineReader"/> (ADR-023 §6/A11) — the pantry-history
/// "does this Purchase row earn the Amend action" batch check. Joins Intake exactly like
/// <see cref="StockProvenanceReaderAdapter"/> joins Intake/Recipes for the same page: Inventory itself
/// never takes a dependency on Intake (Gate 2), the composition root does the join.
/// </summary>
public sealed class AmendableLineReaderAdapter(
    IImportSessionRepository sessions,
    ITenantContext tenant) : IAmendableLineReader
{
    public Task<IReadOnlyDictionary<Guid, Guid>> ResolveAsync(
        IReadOnlyList<Guid> stockEntryIds, CancellationToken ct = default)
    {
        if (stockEntryIds.Count == 0 || tenant.HouseholdId is not { } hid)
            return Task.FromResult<IReadOnlyDictionary<Guid, Guid>>(new Dictionary<Guid, Guid>());

        return sessions.FindCommittedLineIdsByJournalIdsAsync(HouseholdId.From(hid), stockEntryIds, ct);
    }
}
