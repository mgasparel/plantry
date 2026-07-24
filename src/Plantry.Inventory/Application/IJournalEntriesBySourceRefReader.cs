namespace Plantry.Inventory.Application;

/// <summary>
/// A focused, Inventory-owned read over the journal by <c>SourceRef</c> — feeds MealPlanning's cook-status
/// derivation for product dishes (plantry-0eut): a product dish is "eaten" once its net journal movement
/// (summed <c>Delta</c> across every row whose <c>SourceRef</c> = the planned dish id) is negative, i.e. a
/// consuming write was never fully offset by a compensating undo ADD (plantry-zcbx's eat/undo token
/// scheme). Kept as its own port (rather than a method on <see cref="Plantry.Inventory.Domain.IProductStockRepository"/>)
/// because it answers a different, cross-context-facing question — same seam <see cref="IPurchaseJournalReader"/>
/// plays for the Deals stock-up alerts. Household scoping is enforced by the <c>InventoryDbContext</c> RLS
/// query filter, so no household argument is carried.
/// </summary>
public interface IJournalEntriesBySourceRefReader
{
    /// <summary>
    /// Every journal movement (signed delta + when it happened) whose <c>SourceRef</c> is one of
    /// <paramref name="sourceRefs"/>, grouped by that ref. A ref with no matching journal rows is
    /// absent from the result — Inventory never takes a dependency on what a source ref "means"
    /// (that interpretation belongs to the composition-root caller).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<JournalMovement>>> ListBySourceRefsAsync(
        IReadOnlyCollection<Guid> sourceRefs, CancellationToken ct = default);
}

/// <summary>One journal row's movement, projected for netting — no product/unit/reason detail needed.</summary>
/// <param name="Delta">Signed quantity delta (negative = consume, positive = a compensating undo ADD).</param>
/// <param name="OccurredAt">When the movement was recorded.</param>
public sealed record JournalMovement(decimal Delta, DateTimeOffset OccurredAt);
