namespace Plantry.MealPlanning.Application;

/// <summary>
/// Write port onto Inventory for the product-dish "Eat" action (plantry-zcbx, plantry-0eut) — the
/// consume counterpart to <see cref="IMealPlanCookStatusReader"/>'s product-dish derivation. A
/// product dish is "cooked" simply by consuming the product: no CookEvent, no Cook page. Defined
/// here in MealPlanning.Application and <b>implemented in the composition root</b> over Inventory's
/// single consumption primitive (<c>ConsumeStockCommand</c> / <c>ProductStock.Consume</c>) and its
/// intake counterpart (<c>AddStockCommand</c> / <c>ProductStock.AddStock</c>), so MealPlanning never
/// touches Inventory's domain objects or tables directly (Gate 2). All identifiers cross as raw
/// <see cref="Guid"/> soft refs (DM-3).
///
/// <para>
/// Consumed state is never stored by MealPlanning — it is derived entirely from the Inventory journal
/// via <c>IMealPlanCookStatusReader</c> (net signed <c>Delta</c> keyed by <c>SourceRef</c> = the
/// planned dish id). The idempotency token scheme that makes double-tap/crash-retry a no-op while
/// still allowing eat → undo → re-eat is entirely internal to the adapter: MealPlanning only ever
/// says "eat this dish" / "undo eating this dish" with the dish's own identity and the requested
/// quantity — it never sees or manages a token itself.
/// </para>
/// </summary>
public interface IMealPlanEatWriter
{
    /// <summary>
    /// Consumes <paramref name="quantity"/> of the product's default unit from the pantry, stamping
    /// the journal row(s) with <c>SourceRef</c> = <paramref name="plannedDishId"/> so
    /// <c>IMealPlanCookStatusReader</c> derives the dish as eaten. Consume semantics are
    /// consume-available (mirrors C8/R9 — the Recipes cook flow's shortfall tolerance): insufficient
    /// stock never blocks, the call deducts whatever is available and proceeds. A product with no
    /// stock record at all (never purchased/tracked) is tolerated the same way — the call completes
    /// without throwing, though in that specific corner case nothing is actually available to net a
    /// negative journal delta, so the dish's derived state legitimately has nothing to consume.
    ///
    /// <para>
    /// Idempotent by construction: a second call for the same (<paramref name="plannedDishId"/>,
    /// "how many times this dish has been eaten so far") pair — e.g. a double-tap or crash-retry of
    /// the exact same eat — is a no-op that writes no further journal rows. A call following a prior
    /// <see cref="UndoEatAsync"/> is a genuinely new eat (re-eat) and writes a fresh journal row.
    /// </para>
    /// </summary>
    Task EatAsync(
        Guid plannedDishId,
        Guid productId,
        decimal quantity,
        Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// Reverses the most recent eat for <paramref name="plannedDishId"/> with a compensating journal
    /// ADD of the same product/quantity, so the dish's derived state nets back to pending and a
    /// subsequent <see cref="EatAsync"/> call is treated as a fresh re-eat. A no-op when the dish has
    /// no outstanding eat to undo (nothing was ever eaten, or the household never held any stock for
    /// the product in the first place). Idempotent by construction: a double-tap/crash-retry of the
    /// same undo writes no further journal rows.
    /// </summary>
    Task UndoEatAsync(
        Guid plannedDishId,
        Guid productId,
        decimal quantity,
        Guid userId,
        CancellationToken ct = default);
}
