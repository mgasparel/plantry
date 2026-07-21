using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Pricing.Domain;

/// <summary>
/// Flat, append-only aggregate root. One row per observed price event — the immutable price event
/// (price/quantity/unit/source/observed_at/…) is never mutated after creation. The sole sanctioned
/// carve-out is <see cref="StoreId"/>: a late-resolved soft-reference (see its own note) that may be
/// bound once, after the fact, via <see cref="ResolveStore"/> (DM-16 backfill). Nothing else on the row
/// is ever updated.
/// <see cref="UnitPrice"/> is null when the calculator could not normalize (soft-fail, pricing.md resolved-call #2).
/// </summary>
public sealed class PriceObservation : AggregateRoot<PriceObservationId>
{
    public HouseholdId HouseholdId { get; private set; }
    public Guid ProductId { get; private set; }
    public Guid? SkuId { get; private set; }
    public decimal Price { get; private set; }
    public decimal Quantity { get; private set; }
    public Guid UnitId { get; private set; }
    public decimal? UnitPrice { get; private set; }
    public PriceSource Source { get; private set; }
    public string? MerchantText { get; private set; }

    /// <summary>Resolved merchant identity (soft ref → <c>catalog.store</c>). Null for purchases and
    /// until <c>store</c> exists (DM-16); populated by Deals (Phase 5).</summary>
    public Guid? StoreId { get; private set; }

    /// <summary>Deal validity window start (DM-17). Set only for <see cref="PriceSource.Deal"/>;
    /// null for a purchase (a point observation at <see cref="ObservedAt"/>).</summary>
    public DateOnly? ValidFrom { get; private set; }

    /// <summary>Deal validity window end (DM-17) — drives the "cheapest active deal" read model.
    /// Set only for <see cref="PriceSource.Deal"/>; null for a purchase.</summary>
    public DateOnly? ValidTo { get; private set; }

    /// <summary>Provenance soft ref to the writer's record (pricing.md): <c>intake.import_line</c>
    /// (purchase) or <c>deals.deal</c> (deal). Null for <see cref="PriceSource.Manual"/> — a
    /// household-entered estimate has no source document to point at (plantry-3fqm).</summary>
    public Guid? SourceRef { get; private set; }
    public DateTimeOffset ObservedAt { get; private set; }
    public Guid UserId { get; private set; }

    private PriceObservation() { } // EF

    public static PriceObservation Record(
        HouseholdId householdId,
        Guid productId,
        Guid? skuId,
        decimal price,
        decimal quantity,
        Guid unitId,
        decimal? unitPrice,
        PriceSource source,
        string? merchantText,
        Guid? sourceRef,
        DateTimeOffset observedAt,
        Guid userId,
        DateOnly? validFrom = null,
        DateOnly? validTo = null,
        Guid? storeId = null) =>
        new()
        {
            Id = PriceObservationId.New(),
            HouseholdId = householdId,
            ProductId = productId,
            SkuId = skuId,
            Price = price,
            Quantity = quantity,
            UnitId = unitId,
            UnitPrice = unitPrice,
            Source = source,
            MerchantText = merchantText,
            StoreId = storeId,
            ValidFrom = validFrom,
            ValidTo = validTo,
            SourceRef = sourceRef,
            ObservedAt = observedAt,
            UserId = userId,
        };

    /// <summary>
    /// One-time DM-16 late-bind of the resolved merchant identity: sets <see cref="StoreId"/> <b>only</b>
    /// when it is currently null, and touches nothing else on the row (the immutable price event is left
    /// intact). A no-op when a store is already resolved, so the backfill sweep is idempotent and
    /// re-runnable. Returns <see langword="true"/> when it bound a store, <see langword="false"/> when the
    /// observation was already resolved.
    /// </summary>
    public bool ResolveStore(Guid storeId)
    {
        if (StoreId is not null)
            return false;

        StoreId = storeId;
        return true;
    }
}
