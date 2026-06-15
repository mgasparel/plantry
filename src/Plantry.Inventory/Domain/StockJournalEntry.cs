using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Inventory.Domain;

/// <summary>
/// Immutable, append-only record of a single <b>quantity movement</b> (ADR-011 amended, DM-14):
/// the signed <see cref="Delta"/> (+ intake, − consume/waste) in <see cref="UnitId"/>, the
/// <see cref="Reason"/> why-taxonomy, and the orthogonal <see cref="SourceType"/>/<see cref="SourceRef"/>
/// of what triggered it. Rows are never updated or deleted — a correction is a new row, so there
/// is no <c>updated_at</c>. Created only through <see cref="ProductStock"/>.
///
/// <see cref="SourceLineRef"/> is the per-consume-operation idempotency token: when set, a second
/// <see cref="ProductStock.Consume"/> call with the same token is a no-op (no journal rows written,
/// no stock change). The token is per-consume-operation — one consume fans out to N per-lot rows all
/// carrying the same token, and a duplicate-token check against any of those rows short-circuits the
/// whole consume.
/// </summary>
public sealed class StockJournalEntry : Entity<JournalId>
{
    public HouseholdId HouseholdId { get; private set; }
    public Guid ProductId { get; private set; }
    public StockEntryId StockEntryId { get; private set; }
    public decimal Delta { get; private set; }
    public Guid UnitId { get; private set; }
    public StockReason Reason { get; private set; }
    public StockSourceType? SourceType { get; private set; }
    public Guid? SourceRef { get; private set; }

    /// <summary>
    /// Per-consume-operation idempotency token (plantry-292a). Null on manual consume (Pantry Detail
    /// path) and on intake (AddStock). When set on a consume, a subsequent call carrying the same
    /// token against the same aggregate is short-circuited to a no-op inside
    /// <see cref="ProductStock.Consume"/> before any mutation occurs.
    /// </summary>
    public Guid? SourceLineRef { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }
    public Guid UserId { get; private set; }

    private StockJournalEntry() { } // EF

    private StockJournalEntry(
        HouseholdId householdId, Guid productId, StockEntryId stockEntryId, decimal delta,
        Guid unitId, StockReason reason, StockSourceType? sourceType, Guid? sourceRef,
        Guid? sourceLineRef, DateTimeOffset occurredAt, Guid userId)
    {
        Id = JournalId.New();
        HouseholdId = householdId;
        ProductId = productId;
        StockEntryId = stockEntryId;
        Delta = delta;
        UnitId = unitId;
        Reason = reason;
        SourceType = sourceType;
        SourceRef = sourceRef;
        SourceLineRef = sourceLineRef;
        OccurredAt = occurredAt;
        UserId = userId;
    }

    /// <summary>Emitted only by <see cref="ProductStock"/> as part of its unit of work.</summary>
    internal static StockJournalEntry Record(
        HouseholdId householdId, Guid productId, StockEntryId stockEntryId, decimal delta,
        Guid unitId, StockReason reason, StockSourceType? sourceType, Guid? sourceRef,
        Guid? sourceLineRef, DateTimeOffset occurredAt, Guid userId) =>
        new(householdId, productId, stockEntryId, delta, unitId, reason, sourceType, sourceRef, sourceLineRef, occurredAt, userId);
}
