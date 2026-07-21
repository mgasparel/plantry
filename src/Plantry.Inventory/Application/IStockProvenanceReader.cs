using Plantry.Inventory.Domain;

namespace Plantry.Inventory.Application;

/// <summary>Which cross-context surface a resolved <see cref="ProvenanceChip"/> points at — drives the
/// icon the Pantry history grid renders (receipt-intake-history.md H11).</summary>
public enum ProvenanceChipKind
{
    /// <summary>Resolves to a receipt intake session/line — the receipt icon.</summary>
    Intake,

    /// <summary>Resolves to a recipe — the chef's-hat icon.</summary>
    Cook,
}

/// <summary>
/// A resolved, always-linkable provenance affordance for one journal row (receipt-intake-history.md H4).
/// <see cref="Href"/> is never null: a row that cannot be resolved (deleted recipe, foreign ref, no
/// matching line) is simply absent from the reader's result dictionary — the chip is progressive
/// enhancement, never a dead link.
/// </summary>
public sealed record ProvenanceChip(ProvenanceChipKind Kind, string Label, string Href);

/// <summary>
/// Web-defined, Composition-implemented port (receipt-intake-history.md H3/H4): batch-resolves a set of
/// journal rows to their cross-context provenance chip. Declared here (Inventory.Application) rather than
/// in Plantry.Web because the composition-root adapter — which must join Intake and Recipes — cannot
/// reference Plantry.Web (the architecture invariant runs the other way: Web references Composition, never
/// the reverse). The signature itself stays Inventory-shaped (raw ids only), so this project takes no
/// dependency on Intake or Recipes (Gate 2).
///
/// <para>Chip resolution deliberately happens here — at the page boundary, via this port — rather than
/// inside <see cref="InventoryQueryService"/>: Inventory must not read Intake or Recipes directly. The
/// composition-root adapter joins contexts exactly like <c>ShoppingRecipeReaderAdapter</c> does for
/// Shopping; the query service stays context-pure.</para>
/// </summary>
public interface IStockProvenanceReader
{
    /// <summary>
    /// Resolves as many of the given rows as possible to a chip, keyed by each row's <c>JournalId</c>. A
    /// row whose source cannot be resolved (Manual, a deleted recipe, a foreign-household ref, or an
    /// intake line that no longer exists) is simply absent from the result — callers fall back to plain
    /// source text for those. Household-scoped throughout via the ambient tenant context.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ProvenanceChip>> ResolveAsync(
        IReadOnlyList<(Guid JournalId, StockSourceType SourceType, Guid? SourceRef)> rows,
        CancellationToken ct = default);
}
