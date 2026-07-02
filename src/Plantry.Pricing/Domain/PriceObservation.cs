using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Pricing.Domain;

/// <summary>
/// Flat, append-only aggregate root. One row per observed price event — never mutated after creation.
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

    public Guid SourceRef { get; private set; }
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
        Guid sourceRef,
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
}
