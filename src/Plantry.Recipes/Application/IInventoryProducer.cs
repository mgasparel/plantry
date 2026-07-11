namespace Plantry.Recipes.Application;

/// <summary>
/// Write port onto Inventory for yield-on-cook (plantry-854a, recipe-composition.md §9) — the produce
/// counterpart to <see cref="IInventoryConsumer"/>. The mechanism by which the Cook flow ADDS a lot of a
/// recipe's yield product to the pantry (leftover / prepped stock) without the Recipes context ever
/// touching Inventory's domain objects or tables directly (ADR-011). Defined here in Recipes.Application
/// and <b>implemented in the composition root</b> over Inventory's <c>AddStockCommand</c>, so the Recipes
/// projects stay <c>→ SharedKernel only</c>. All identifiers cross as raw <see cref="Guid"/> soft refs (DM-3).
/// </summary>
public interface IInventoryProducer
{
    /// <summary>
    /// Adds <paramref name="quantity"/> of <paramref name="unitId"/> as a new lot of
    /// <paramref name="productId"/> to the pantry, stamping the journal row with
    /// <paramref name="reason"/>, <paramref name="userId"/>, and <paramref name="cookEventId"/> as the
    /// source reference, and materialising the lot with <paramref name="expiryDate"/> (null for none).
    /// The lot's storage location is resolved by the adapter (Recipes has no location concept).
    ///
    /// <paramref name="sourceLineRef"/> is the per-produce-operation idempotency token: the
    /// <c>CookProduceLine.Id</c> (a per-cook-unique <see cref="Guid"/>). The Inventory add short-circuits
    /// to a no-op when a journal row already carries the (<paramref name="cookEventId"/>,
    /// <paramref name="sourceLineRef"/>) pair, so a re-driven produce (reconciliation after an interrupted
    /// cook) never adds the lot twice — the produce counterpart to the consume idempotency guarantee
    /// (plantry-292a / plantry-fks).
    ///
    /// Throws <see cref="InvalidOperationException"/> when the add cannot be recorded (the product does not
    /// exist, cannot hold stock, or no storage location is available) — the cook flow records the produce
    /// line <c>Failed</c> and proceeds.
    /// </summary>
    Task ProduceAsync(
        Guid productId,
        decimal quantity,
        Guid unitId,
        DateOnly? expiryDate,
        ProduceReason reason,
        Guid cookEventId,
        Guid userId,
        Guid sourceLineRef,
        CancellationToken ct = default);
}

/// <summary>
/// The why-axis exposed to the Recipes context for produces — deliberately narrow (only the produce
/// reason that makes sense here). Mapped to Inventory's addition reason inside the adapter.
/// </summary>
public enum ProduceReason
{
    /// <summary>Yield stored while cooking a recipe (leftovers / batch prep).</summary>
    Recipe,
}
