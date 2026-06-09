using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Inventory.Domain;

/// <summary>
/// A single lot of stock — the child of <see cref="ProductStock"/> (inventory.md). Carries the
/// remaining <see cref="Quantity"/> in <see cref="UnitId"/>, its <see cref="LocationId"/>, and the
/// expiry <b>materialized at event time</b> (DM-11) — never recomputed at read time.
/// <see cref="FrozenAt"/>/<see cref="ThawedAt"/>/<see cref="IsOpen"/> exist now but the transitions
/// that drive them are Slice 3. A depleted lot keeps its row (<see cref="DepletedAt"/> set) because
/// the append-only journal's FK requires every historical lot to stay live.
/// </summary>
public sealed class StockEntry : Entity<StockEntryId>
{
    public HouseholdId HouseholdId { get; private set; }
    public Guid ProductId { get; private set; }
    public Guid? SkuId { get; private set; }
    public decimal Quantity { get; private set; }
    public Guid UnitId { get; private set; }
    public Guid LocationId { get; private set; }
    public DateOnly? ExpiryDate { get; private set; }
    public bool IsOpen { get; private set; }
    public DateTimeOffset? FrozenAt { get; private set; }
    public DateTimeOffset? ThawedAt { get; private set; }
    public DateOnly? PurchasedAt { get; private set; }
    public DateTimeOffset? DepletedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private StockEntry() { } // EF

    private StockEntry(
        HouseholdId householdId, Guid productId, Guid? skuId, decimal quantity, Guid unitId,
        Guid locationId, DateOnly? expiryDate, DateOnly? purchasedAt, DateTimeOffset now)
    {
        Id = StockEntryId.New();
        HouseholdId = householdId;
        ProductId = productId;
        SkuId = skuId;
        Quantity = quantity;
        UnitId = unitId;
        LocationId = locationId;
        ExpiryDate = expiryDate;
        PurchasedAt = purchasedAt;
        CreatedAt = now;
        UpdatedAt = now;
    }

    /// <summary>Created only through <see cref="ProductStock.AddStock"/> — keeps the aggregate boundary intact.</summary>
    internal static StockEntry Create(
        HouseholdId householdId, Guid productId, Guid? skuId, decimal quantity, Guid unitId,
        Guid locationId, DateOnly? expiryDate, DateOnly? purchasedAt, DateTimeOffset now) =>
        new(householdId, productId, skuId, quantity, unitId, locationId, expiryDate, purchasedAt, now);

    public bool IsDepleted => DepletedAt is not null;
    public bool IsActive => DepletedAt is null && Quantity > 0m;

    /// <summary>
    /// Removes <paramref name="amount"/> (in this lot's unit) from the lot. Called only by the root
    /// after it has confirmed the amount does not exceed <see cref="Quantity"/>. Reaching zero marks
    /// the lot depleted but retains the row for journal integrity.
    /// </summary>
    internal void Deduct(decimal amount, IClock clock)
    {
        if (amount <= 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), "Deduction must be positive.");
        if (amount > Quantity)
            throw new InvalidOperationException("Cannot deduct more than the lot holds.");

        var now = clock.UtcNow;
        Quantity -= amount;
        if (Quantity <= 0m)
        {
            Quantity = 0m;
            DepletedAt = now;
        }
        UpdatedAt = now;
    }
}
