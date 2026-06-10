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
        Guid userId) =>
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
            SourceRef = sourceRef,
            ObservedAt = observedAt,
            UserId = userId,
        };
}
