namespace Plantry.Pantry.Application;

/// <summary>
/// A focused, Inventory-owned read over the journal by <c>SourceRef</c> — feeds MealPlanning's cook-status
/// derivation for product dishes (plantry-0eut): a product dish is "eaten" once its net journal movement
/// (summed <c>Delta</c> across every row whose <c>SourceRef</c> = the planned dish id) is negative, i.e. a
/// consuming write was never fully offset by a compensating undo ADD (plantry-zcbx's eat/undo token
/// scheme). Kept as its own port (rather than a method on <see cref="Plantry.Pantry.Domain.IProductStockRepository"/>)
/// because it answers a different, cross-context-facing question — same seam <see cref="IPurchaseJournalReader"/>
/// plays for the Deals stock-up alerts. Household scoping is enforced by the <c>PantryDbContext</c> RLS
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

/// <summary>
/// One journal row's movement, projected for netting — no product/reason detail needed, but
/// <see cref="UnitId"/> is carried (plantry-vqa7) so a caller deriving a displayable consumed
/// quantity can detect whether every movement for a source ref shares one unit (safe to sum) or
/// spans more than one (the raw net is not a displayable magnitude — see
/// <c>MealPlanEatWriterAdapter</c>'s doc comment on why undo mirrors per-lot units).
/// </summary>
/// <param name="Delta">Signed quantity delta (negative = consume, positive = a compensating undo ADD).</param>
/// <param name="OccurredAt">When the movement was recorded.</param>
/// <param name="UnitId">The unit this row's <see cref="Delta"/> is denominated in.</param>
public sealed record JournalMovement(decimal Delta, DateTimeOffset OccurredAt, Guid UnitId);
