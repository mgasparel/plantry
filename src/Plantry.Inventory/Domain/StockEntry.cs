using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Inventory.Domain;

/// <summary>
/// A single lot of stock — the child of <see cref="ProductStock"/> (inventory.md). Carries the
/// remaining <see cref="Quantity"/> in <see cref="UnitId"/>, its <see cref="LocationId"/>, and the
/// expiry <b>materialized at event time</b> (DM-11) — never recomputed at read time.
/// <see cref="IsOpen"/>'s transitions are driven by <see cref="ProductStock.MarkOpened"/> /
/// <see cref="ProductStock.UnmarkOpened"/> (plantry-1le6); <see cref="FrozenAt"/>/<see cref="ThawedAt"/>
/// are driven by <see cref="ProductStock.Transfer"/> (plantry-6owm) — each field independently records
/// its own most-recent occurrence (no transition-history table in v1). A depleted lot
/// keeps its row (<see cref="DepletedAt"/> set) because the append-only journal's FK requires every
/// historical lot to stay live.
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

    /// <summary>
    /// Adds <paramref name="amount"/> (in this lot's unit) to the lot — the counterpart to
    /// <see cref="Deduct"/>, used only by <see cref="ProductStock.AmendPurchase"/> (ADR-023) to
    /// apply a positive compensating delta to the <b>same</b> lot (never a new one). Un-depleting an
    /// already-depleted lot stays out of scope for v1 — the root confirms <see cref="IsActive"/>
    /// before calling. Called only by the root, keeping the aggregate boundary intact.
    /// </summary>
    internal void Increase(decimal amount, IClock clock)
    {
        if (amount <= 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), "Increase must be positive.");

        Quantity += amount;
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Flips the lot open and applies <paramref name="expiryDate"/> — the already-clamped value
    /// <see cref="ProductStock.MarkOpened"/> (or the auto-open step of <see cref="ProductStock.Consume"/>,
    /// plantry-1le6 rule 5) computed. Called only by the root, keeping the aggregate boundary intact.
    /// </summary>
    internal void MarkOpen(DateOnly? expiryDate, IClock clock)
    {
        IsOpen = true;
        ExpiryDate = expiryDate;
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Un-marks an opened lot (plantry-1le6 rule 3) — a correction, not a recompute: the expiry that
    /// opening replaced is <b>not</b> restored (no history is kept of it). Called only by the root.
    /// </summary>
    internal void UnmarkOpen(IClock clock)
    {
        IsOpen = false;
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Moves this lot in place to <paramref name="locationId"/> (plantry-6owm rule 1 — full-lot
    /// transfer) and applies the already-decided <paramref name="expiryDate"/> (the caller has already
    /// run the freeze/thaw recompute or left it unchanged for a plain move). <see cref="FrozenAt"/> /
    /// <see cref="ThawedAt"/> record their <b>own</b> most-recent occurrence independently (rule 4 —
    /// no transition-history table): a <see cref="TransferKind.Freeze"/> sets <see cref="FrozenAt"/>
    /// without touching <see cref="ThawedAt"/>, and vice versa for <see cref="TransferKind.Thaw"/>; a
    /// plain <see cref="TransferKind.Move"/> touches neither. Called only by the root.
    /// </summary>
    internal void MoveTo(Guid locationId, DateOnly? expiryDate, TransferKind kind, DateTimeOffset now)
    {
        LocationId = locationId;
        ExpiryDate = expiryDate;
        if (kind == TransferKind.Freeze) FrozenAt = now;
        else if (kind == TransferKind.Thaw) ThawedAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Reduces this lot's quantity for a partial transfer (plantry-6owm rule 1 — the split path): the
    /// remainder stays at this lot's current location, keeping its expiry and FrozenAt/ThawedAt
    /// untouched — only the moved portion (a new lot, see <see cref="CreateSplit"/>) goes through the
    /// freeze/thaw recompute. Unlike <see cref="Deduct"/>, a split can never deplete the source lot —
    /// the root only calls this when <paramref name="amount"/> is strictly less than <see cref="Quantity"/>.
    /// Called only by the root.
    /// </summary>
    internal void ReduceForSplit(decimal amount, IClock clock)
    {
        if (amount <= 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), "Split amount must be positive.");
        if (amount >= Quantity)
            throw new InvalidOperationException("A split must leave a remainder on the source lot.");

        Quantity -= amount;
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Creates the new lot a partial transfer's moved portion becomes (plantry-6owm rule 1) —
    /// inherits <paramref name="source"/>'s household/product/sku/unit/PurchasedAt, lands at
    /// <paramref name="destinationLocationId"/> with the already-recomputed <paramref name="expiryDate"/>,
    /// and sets FrozenAt/ThawedAt per <paramref name="kind"/> (mirroring <see cref="MoveTo"/>'s own-field
    /// independence — the new lot starts with neither set, then picks up whichever this transfer's kind
    /// implies). Called only by the root, keeping the aggregate boundary intact.
    /// </summary>
    internal static StockEntry CreateSplit(
        StockEntry source, decimal quantity, Guid destinationLocationId, DateOnly? expiryDate,
        TransferKind kind, DateTimeOffset now)
    {
        var entry = new StockEntry(
            source.HouseholdId, source.ProductId, source.SkuId, quantity, source.UnitId,
            destinationLocationId, expiryDate, source.PurchasedAt, now);
        if (kind == TransferKind.Freeze) entry.FrozenAt = now;
        else if (kind == TransferKind.Thaw) entry.ThawedAt = now;
        return entry;
    }
}
