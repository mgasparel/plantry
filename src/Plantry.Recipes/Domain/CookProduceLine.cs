using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Recipes.Domain;

/// <summary>
/// A planned yield-on-cook inventory ADD that is a child of <see cref="CookEvent"/> (plantry-854a) —
/// the produce counterpart to <see cref="CookConsumeLine"/>. Persisted in
/// <c>recipes.cook_produce_line</c> as the durable record of what a cook intended to STORE as leftover
/// or prepped stock. Created in <see cref="CookProduceLineStatus.Pending"/> before any inventory call
/// runs (anchor-first, joining the 292b protocol), then transitioned to
/// <see cref="CookProduceLineStatus.Applied"/> or <see cref="CookProduceLineStatus.Failed"/> after
/// <c>IInventoryProducer.ProduceAsync</c> returns.
/// <para>
/// The <c>sourceLineRef</c> passed to <c>IInventoryProducer.ProduceAsync</c> is this line's own
/// <see cref="Entity{TId}.Id"/> value — a per-cook-unique guid — so a re-driven produce is idempotent
/// against the Inventory journal's (source_ref, source_line_ref) token exactly as a consume re-drive is
/// (plantry-292a / plantry-fks). All identifiers are bare <see cref="Guid"/> soft-refs (DM-3).
/// </para>
/// </summary>
public sealed class CookProduceLine : Entity<CookProduceLineId>
{
    public HouseholdId HouseholdId { get; private set; }
    public CookEventId CookEventId { get; private set; }

    /// <summary>The yield product to add stock of (soft-ref cross-context, DM-3).</summary>
    public Guid ProductId { get; private set; }

    /// <summary>Quantity to store (numeric(12,3)); the user-supplied stored amount at cook time.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>Unit of <see cref="Quantity"/> — the recipe's declared yield unit (soft-ref, DM-3).</summary>
    public Guid UnitId { get; private set; }

    /// <summary>User-supplied use-by date for the stored lot; null when the user gave no expiry.</summary>
    public DateOnly? ExpiryDate { get; private set; }

    /// <summary>Current lifecycle state — starts <see cref="CookProduceLineStatus.Pending"/>.</summary>
    public CookProduceLineStatus Status { get; private set; }

    private CookProduceLine() { } // EF

    internal CookProduceLine(
        CookProduceLineId id,
        HouseholdId householdId,
        CookEventId cookEventId,
        Guid productId,
        decimal quantity,
        Guid unitId,
        DateOnly? expiryDate)
    {
        Id = id;
        HouseholdId = householdId;
        CookEventId = cookEventId;
        ProductId = productId;
        Quantity = quantity;
        UnitId = unitId;
        ExpiryDate = expiryDate;
        Status = CookProduceLineStatus.Pending;
    }

    /// <summary>
    /// Transitions the line to <see cref="CookProduceLineStatus.Applied"/> after a successful
    /// <c>ProduceAsync</c> call (or an idempotent no-op re-drive). Idempotent if already Applied.
    /// </summary>
    public void MarkApplied() => Status = CookProduceLineStatus.Applied;

    /// <summary>
    /// Transitions the line to the terminal <see cref="CookProduceLineStatus.Failed"/> state when the
    /// inventory add could not be recorded (the yield product does not exist or cannot hold stock).
    /// Reconciliation never re-drives a Failed line.
    /// </summary>
    public void MarkFailed() => Status = CookProduceLineStatus.Failed;
}
