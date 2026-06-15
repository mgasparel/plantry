namespace Plantry.Recipes.Application;

/// <summary>
/// Write port onto Inventory (recipes-domain-model.md §8) — the mechanism by which the Cook flow
/// decrements the pantry without the Recipes context ever touching Inventory's domain objects or
/// tables directly (ADR-011). Defined here in Recipes.Application; <b>implemented in Plantry.Web</b>
/// over Inventory's <c>ConsumeStockCommand</c> (the single consumption primitive), so the Recipes
/// projects stay <c>→ SharedKernel only</c>.
///
/// Consume semantics are <em>consume-available</em>: the call never blocks or throws when stock is
/// insufficient — it deducts what is available and reports any <see cref="ConsumeResult.ShortfallAmount"/>
/// in the requested unit. All identifiers cross as raw <see cref="Guid"/> soft refs (DM-3).
/// </summary>
public interface IInventoryConsumer
{
    /// <summary>
    /// Deducts <paramref name="quantity"/> of <paramref name="unitId"/> from the pantry stock of
    /// <paramref name="productId"/> (or its first matching variant), stamping the journal row with
    /// <paramref name="reason"/>, <paramref name="userId"/>, and <paramref name="cookEventId"/> as
    /// the source reference. Returns a <see cref="ConsumeResult"/> that reports any shortfall; never
    /// throws on shortfall. Throws <see cref="InvalidOperationException"/> when the product has no
    /// stock record at all (no lots ever added).
    ///
    /// <paramref name="sourceLineRef"/> is the per-consume-operation idempotency token (plantry-292a /
    /// plantry-fks): the <c>CookConsumeLine.Id</c> (a per-cook-unique <see cref="Guid"/>) that
    /// uniquely identifies this consume within one cook. The guard scopes by
    /// (<paramref name="cookEventId"/>, <paramref name="sourceLineRef"/>) — both must match an
    /// existing journal row for the short-circuit to fire. When it does, the call is a no-op:
    /// no further journal rows are written and stock is not changed. Do NOT pass the ingredient id
    /// — that is stable across every cook of a recipe and would cause a second cook of the same
    /// recipe to skip its consume.
    /// </summary>
    Task<ConsumeResult> ConsumeAsync(
        Guid productId,
        decimal quantity,
        Guid unitId,
        ConsumeReason reason,
        Guid cookEventId,
        Guid userId,
        Guid sourceLineRef,
        CancellationToken ct = default);
}

/// <summary>
/// The why-axis exposed to the Recipes context — deliberately narrow (only consumption reasons
/// that make sense here) so the Recipes domain does not know about <c>StockReason.Purchase</c>
/// or <c>StockReason.Correction</c>. Mapped to <c>StockReason</c> inside the adapter.
/// </summary>
public enum ConsumeReason
{
    /// <summary>Ingredients consumed while cooking a recipe.</summary>
    Recipe,
}

/// <summary>
/// The outcome of a <see cref="IInventoryConsumer.ConsumeAsync"/> call. A shortfall is reported,
/// never an over-deduction (ADR-011). <see cref="ShortfallAmount"/> is zero when the pantry
/// covered the full quantity.
/// </summary>
/// <param name="ShortfallAmount">
/// The quantity (in the requested unit) that could not be satisfied because the pantry had
/// insufficient stock. Zero means fully satisfied.
/// </param>
/// <param name="RequestUnitId">The unit in which <see cref="ShortfallAmount"/> is expressed.</param>
public sealed record ConsumeResult(decimal ShortfallAmount, Guid RequestUnitId)
{
    public bool HasShortfall => ShortfallAmount > 0m;
}
