using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Recipes.Domain;

/// <summary>
/// A planned consume operation that is a child of <see cref="CookEvent"/> (plantry-292b).
/// Persisted in <c>recipes.cook_consume_line</c> as the durable plan that records what a cook
/// intended to consume. Created in <see cref="CookConsumeLineStatus.Pending"/> before any
/// inventory call runs (anchor-first, 292b), then transitioned to
/// <see cref="CookConsumeLineStatus.Applied"/> or <see cref="CookConsumeLineStatus.Shorted"/>
/// after <c>IInventoryConsumer.ConsumeAsync</c> returns.
/// <para>
/// <see cref="IngredientId"/> is the idempotency token passed to
/// <c>IInventoryConsumer.ConsumeAsync</c> as <c>sourceLineRef</c> (292a). It is a bare
/// <see cref="Guid"/> soft-ref — no FK to <c>recipe_ingredient</c>, consistent with DM-3.
/// </para>
/// </summary>
public sealed class CookConsumeLine : Entity<CookConsumeLineId>
{
    public HouseholdId HouseholdId { get; private set; }
    public CookEventId CookEventId { get; private set; }

    /// <summary>
    /// The ingredient this line resolves (soft-ref, DM-3).
    /// Doubles as the <c>sourceLineRef</c> idempotency token on the Inventory consume call (292a).
    /// </summary>
    public Guid IngredientId { get; private set; }

    /// <summary>The product to consume (soft-ref cross-context, DM-3).</summary>
    public Guid ProductId { get; private set; }

    /// <summary>Scaled quantity to consume (numeric(12,3)).</summary>
    public decimal Quantity { get; private set; }

    /// <summary>Unit of <see cref="Quantity"/> (soft-ref, DM-3).</summary>
    public Guid UnitId { get; private set; }

    /// <summary>Current lifecycle state — starts <see cref="CookConsumeLineStatus.Pending"/>.</summary>
    public CookConsumeLineStatus Status { get; private set; }

    /// <summary>
    /// Shortfall quantity in the requested unit.  Zero until the line is resolved.
    /// Populated by <see cref="MarkApplied"/> or <see cref="MarkShorted"/>.
    /// </summary>
    public decimal Shortfall { get; private set; }

    private CookConsumeLine() { } // EF

    internal CookConsumeLine(
        CookConsumeLineId id,
        HouseholdId householdId,
        CookEventId cookEventId,
        Guid ingredientId,
        Guid productId,
        decimal quantity,
        Guid unitId)
    {
        Id = id;
        HouseholdId = householdId;
        CookEventId = cookEventId;
        IngredientId = ingredientId;
        ProductId = productId;
        Quantity = quantity;
        UnitId = unitId;
        Status = CookConsumeLineStatus.Pending;
        Shortfall = 0m;
    }

    /// <summary>
    /// Transitions the line to <see cref="CookConsumeLineStatus.Applied"/> after a successful
    /// (possibly partial) <c>ConsumeAsync</c> call. Idempotent if already Applied.
    /// </summary>
    /// <param name="shortfall">The shortfall amount reported by the inventory consumer (zero when
    /// fully satisfied).</param>
    public void MarkApplied(decimal shortfall)
    {
        Status = CookConsumeLineStatus.Applied;
        Shortfall = shortfall;
    }

    /// <summary>
    /// Transitions the line to <see cref="CookConsumeLineStatus.Shorted"/> when the product had
    /// no stock record at all (<see cref="InvalidOperationException"/> from the consumer) or was
    /// fully unavailable. The full requested quantity is recorded as the shortfall.
    /// </summary>
    public void MarkShorted()
    {
        Status = CookConsumeLineStatus.Shorted;
        Shortfall = Quantity;
    }
}
