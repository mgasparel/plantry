namespace Plantry.Inventory.Application;

/// <summary>
/// Web-defined, Composition-implemented port (ADR-023 §6/A11) — batch-resolves which Purchase journal
/// rows on a Pantry Product Detail History grid are intake-sourced and therefore earn the "Amend" row
/// action, mirroring <see cref="IStockProvenanceReader"/>'s shape/rationale for the same page: the
/// composition-root adapter joins Intake (which Inventory itself must not depend on, Gate 2), Inventory
/// stays context-pure.
///
/// <para>Keyed by <c>StockEntryId</c> (<see cref="StockJournalRow.StockEntryId"/> for a Purchase row) —
/// the value <c>ImportLine.JournalId</c> was stamped with at commit (ADR-023 §1). NOT the same
/// correlation key <see cref="IStockProvenanceReader"/> uses for its chip label (that resolves off
/// <c>SourceRef</c>/the journal row's own id) — this is the reverse-lookup <c>GetCommittedLineByJournalIdQuery</c>
/// plays for the sheet itself, batched for the grid's render pass.</para>
/// </summary>
public interface IAmendableLineReader
{
    /// <summary>
    /// Resolves as many of the given lot ids as possible to the committed <c>ImportLine</c> id that
    /// produced them, keyed by the lot id (<c>StockEntryId</c>). A lot id absent from the result offers
    /// no Amend action — a manually-added lot, or one whose committing line cannot be found. Household-
    /// scoped throughout via the ambient tenant context.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Guid>> ResolveAsync(
        IReadOnlyList<Guid> stockEntryIds, CancellationToken ct = default);
}
