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
/// <see cref="IngredientId"/> is a bare <see cref="Guid"/> soft-ref — no FK to
/// <c>recipe_ingredient</c>, consistent with DM-3. It is NOT the idempotency token: the
/// <c>sourceLineRef</c> passed to <c>IInventoryConsumer.ConsumeAsync</c> is this line's own
/// <see cref="Entity{TId}.Id"/> value — a per-cook-unique guid — so that two cooks of the same
/// recipe each get an independent token (plantry-fks).
/// </para>
/// </summary>
public sealed class CookConsumeLine : Entity<CookConsumeLineId>
{
    public HouseholdId HouseholdId { get; private set; }
    public CookEventId CookEventId { get; private set; }

    /// <summary>
    /// The ingredient this line resolves (soft-ref, DM-3).
    /// NOT the idempotency token — see <see cref="Entity{TId}.Id"/> for the per-cook-unique token
    /// passed as <c>sourceLineRef</c> on the Inventory consume call (plantry-fks).
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

    /// <summary>
    /// Transitions the line to <see cref="CookConsumeLineStatus.DeferredUnitGap"/> when the consume
    /// could not run because no <c>ProductConversion</c> bridged the ingredient unit to the product's
    /// stock unit (<c>Catalog.UnresolvableConversion</c>) — plantry-qll2.6. This is NOT a shortfall:
    /// the consume planning pass fails atomically before any lot mutation, so the pantry is untouched.
    /// The full requested quantity is recorded as the outstanding amount owed; it is overwritten by
    /// <see cref="MarkApplied"/> when the deferred consume is retro-applied once a conversion lands.
    /// Idempotent if already <see cref="CookConsumeLineStatus.DeferredUnitGap"/>.
    /// </summary>
    public void MarkDeferredUnitGap()
    {
        Status = CookConsumeLineStatus.DeferredUnitGap;
        Shortfall = Quantity;
    }

    /// <summary>
    /// Voids a <see cref="CookConsumeLineStatus.DeferredUnitGap"/> line by transitioning it to the
    /// terminal <see cref="CookConsumeLineStatus.SupersededByCount"/> state (plantry-qll2.6). Called
    /// when an absolute observation (Take Stock count / manual absolute adjustment) on the product
    /// captured reality directly, superseding this relative deferred delta — retro-applying afterwards
    /// would double-count. Only a currently-deferred line is voided; no-op otherwise so an already
    /// Applied/Shorted/superseded line is never disturbed.
    /// </summary>
    public void MarkSupersededByCount()
    {
        if (Status != CookConsumeLineStatus.DeferredUnitGap)
            return;
        Status = CookConsumeLineStatus.SupersededByCount;
        Shortfall = 0m;
    }
}
