using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Inventory.Domain;

/// <summary>
/// The Inventory aggregate root (inventory.md, ADR-010) — one per product per household, keyed
/// <c>(household_id, product_id)</c>. Owns its <see cref="StockEntry"/> lots and emits the immutable
/// <see cref="StockJournalEntry"/> rows for every quantity movement. It is the concurrency anchor:
/// a multi-lot FEFO consume serializes on this one row (<c>FOR UPDATE</c> in the repository), with
/// <c>xmin</c> as the optimistic backstop.
///
/// All stock removal flows through the single <see cref="Consume"/> primitive (ADR-011); intake
/// flows through <see cref="AddStock"/>. Transfer/freeze/thaw/open are Slice 3.
/// </summary>
public sealed class ProductStock : AggregateRoot<ProductStockId>
{
    public HouseholdId HouseholdId { get; private set; }
    public Guid ProductId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// The per-household, per-product low stock threshold (i.e. the "running low at" quantity).
    /// Null or zero means no threshold is set — <see cref="IsRunningLow"/> is always false in that case.
    /// When set, <see cref="IsRunningLow"/> is true when total on-hand ≤ this threshold.
    /// Belongs in Inventory (household-specific setting), NOT in Catalog.
    /// </summary>
    public decimal? LowStockThreshold { get; private set; }

    /// <summary>
    /// Derives the running-low state from the persisted <see cref="LowStockThreshold"/> and the supplied
    /// <paramref name="onHand"/> quantity. Null/zero threshold → always false; onHand ≤ threshold → true.
    /// </summary>
    public bool IsRunningLow(decimal onHand) =>
        LowStockThreshold is { } t && t > 0m && onHand <= t;

    /// <summary>Sets or clears the low stock threshold for this product in this household, and bumps
    /// <see cref="UpdatedAt"/> to the clock's current instant — matching the house style of
    /// <see cref="AddStock"/> and <see cref="Consume"/>.</summary>
    public void SetLowStockThreshold(decimal? threshold, IClock clock)
    {
        if (threshold < 0m)
            throw new ArgumentOutOfRangeException(nameof(threshold), "Low stock threshold must be non-negative.");
        LowStockThreshold = threshold;
        UpdatedAt = clock.UtcNow;
    }

    private readonly List<StockEntry> _entries = [];
    private readonly List<StockJournalEntry> _journal = [];
    public IReadOnlyList<StockEntry> Entries => _entries.AsReadOnly();
    public IReadOnlyList<StockJournalEntry> Journal => _journal.AsReadOnly();

    private ProductStock() { } // EF

    private ProductStock(HouseholdId householdId, Guid productId, DateTimeOffset now)
    {
        HouseholdId = householdId;
        ProductId = productId;
        Id = new ProductStockId(householdId.Value, productId);
        CreatedAt = now;
        UpdatedAt = now;
    }

    /// <summary>Begins tracking stock for a product in a household (no lots yet).</summary>
    public static ProductStock Start(HouseholdId householdId, Guid productId, IClock clock) =>
        new(householdId, productId, clock.UtcNow);

    /// <summary>The lots that can still be consumed, in FEFO order (the order <see cref="Consume"/> uses).</summary>
    public IEnumerable<StockEntry> ActiveLotsFefo() => OrderFefo(_entries.Where(e => e.IsActive));

    /// <summary>
    /// FEFO total order (inventory.md resolved-call #3): soonest expiry first, <b>nulls last</b>
    /// (null = "no expiry", consumed last — not "unknown"), then <c>created_at</c>, then
    /// <c>entry_id</c> so the order is total and deterministic even for lots that share an expiry
    /// and creation instant (e.g. a bulk intake commit).
    /// </summary>
    private static IEnumerable<StockEntry> OrderFefo(IEnumerable<StockEntry> lots) =>
        lots
            .OrderBy(e => e.ExpiryDate is null)          // false (0) before true (1) ⇒ nulls last
            .ThenBy(e => e.ExpiryDate ?? DateOnly.MaxValue)
            .ThenBy(e => e.CreatedAt)
            .ThenBy(e => e.Id.Value);

    /// <summary>
    /// Records intake of a new lot (DM-11/13): creates a <see cref="StockEntry"/> and appends a
    /// positive journal row. The <paramref name="reason"/> defaults to <c>Purchase</c> (normal
    /// intake) but may be <c>Correction</c> when Take Stock discovers more stock than recorded
    /// (Phase 4 / P4-1, TS-2/C8). Only reasons that pass <see cref="StockReasonExtensions.IsAddition"/>
    /// are permitted; passing a removal reason throws <see cref="ArgumentException"/>.
    /// <paramref name="expiryDate"/> is already materialized by the caller (the expiry-default
    /// chain runs at the page/application boundary).
    /// </summary>
    public StockEntry AddStock(
        decimal quantity, Guid unitId, Guid locationId, Guid userId, IClock clock,
        Guid? skuId = null, DateOnly? expiryDate = null, DateOnly? purchasedAt = null,
        StockSourceType sourceType = StockSourceType.Manual, Guid? sourceRef = null,
        StockReason reason = StockReason.Purchase, Guid? sourceLineRef = null)
    {
        if (quantity <= 0m)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Intake quantity must be positive.");
        if (!reason.IsAddition())
            throw new ArgumentException($"AddStock cannot record a {reason} reason; only Purchase or Correction are addition reasons.", nameof(reason));

        // Idempotency short-circuit (yield-on-cook, plantry-854a) — the ADD counterpart to the Consume
        // guard (plantry-292a / plantry-fks). When a sourceLineRef token is supplied and any journal row
        // already carries this (sourceRef, sourceLineRef) pair, the lot was already added by the original
        // produce — return the existing lot without adding a second one. This lets ReconcilePendingCooks
        // re-drive a Pending produce line after an interrupted cook without double-adding stock. Both
        // dimensions are required: sourceLineRef alone is not unique across cooks, so sourceRef (the
        // CookEvent id) scopes the check, matching the (household_id, source_ref, source_line_ref) index.
        // Callers with no token (manual/intake add) pass null and are unaffected.
        if (sourceLineRef is { } token)
        {
            var priorRow = _journal.FirstOrDefault(j => j.SourceLineRef == token && j.SourceRef == sourceRef);
            if (priorRow is not null)
            {
                var existing = _entries.FirstOrDefault(e => e.Id == priorRow.StockEntryId);
                if (existing is not null)
                    return existing; // no-op: this produce already added its lot
            }
        }

        var now = clock.UtcNow;
        var entry = StockEntry.Create(HouseholdId, ProductId, skuId, quantity, unitId, locationId, expiryDate, purchasedAt, now);
        _entries.Add(entry);
        _journal.Add(StockJournalEntry.Record(
            HouseholdId, ProductId, entry.Id, +quantity, unitId,
            reason, sourceType, sourceRef, sourceLineRef: sourceLineRef, now, userId));
        UpdatedAt = now;
        return entry;
    }

    /// <summary>
    /// The single consumption primitive (ADR-011). Converts <paramref name="amount"/> from
    /// <paramref name="unitId"/> into each lot's unit, deducts FEFO across lots (or only
    /// <paramref name="targetEntry"/> when set — "this carton is empty/spoiled"), writes one signed
    /// removal journal row per lot touched, and reports any <see cref="ConsumeOutcome.ShortfallAmount"/>.
    ///
    /// When <paramref name="locationId"/> is supplied, FEFO is scoped to lots in that Location only —
    /// lots in other Locations are invisible to this consume (Phase 4 / P4-1, TS-3). When null,
    /// FEFO runs across all active lots (existing behaviour).
    ///
    /// Conversion is resolved in a planning pass <b>before</b> any lot is mutated, so an unresolvable
    /// unit fails loudly with the <see cref="IQuantityConverter"/>'s <see cref="Error"/> and leaves
    /// the aggregate untouched. Never over-deducts.
    ///
    /// When <paramref name="sourceLineRef"/> is supplied alongside <paramref name="sourceRef"/>,
    /// the method first checks whether any existing journal row already carries the
    /// (<paramref name="sourceRef"/>, <paramref name="sourceLineRef"/>) pair. If so, it
    /// short-circuits to a no-op without mutating any lot or writing any journal row — this is the
    /// idempotency guarantee (plantry-292a / plantry-fks). Both dimensions are required: a
    /// <paramref name="sourceLineRef"/> alone is not unique across cooks (the same ingredient id
    /// recurs in every cook of a recipe), so <paramref name="sourceRef"/> (the CookEvent id)
    /// scopes the check to one cook. Instead of returning zero shortfall, the short-circuit
    /// recomputes the original shortfall from the matching journal rows so that
    /// <c>ReconcilePendingCooks</c> preserves real partial shortfalls on re-drive.
    /// </summary>
    public Result<ConsumeOutcome> Consume(
        decimal amount, Guid unitId, StockReason reason, IQuantityConverter converter,
        Guid userId, IClock clock, Guid? sourceRef = null, StockSourceType? sourceType = null,
        StockEntryId? targetEntry = null, Guid? sourceLineRef = null, Guid? locationId = null)
    {
        if (amount <= 0m)
            return Error.Custom("Inventory.InvalidConsumeAmount", "Consume amount must be positive.");
        if (!reason.IsRemoval())
            return Error.Custom("Inventory.InvalidConsumeReason", "Consume cannot record a Purchase; use AddStock.");

        // Idempotency short-circuit (plantry-292a / plantry-fks fix 2): if any journal row already
        // carries this (sourceRef, sourceLineRef) pair, the consume was already applied — return a
        // no-op outcome without mutating any lot. Both dimensions are required: sourceLineRef alone
        // is not unique across cooks (a recipe's ingredient appears in every cook), so we scope by
        // sourceRef (the CookEvent id) as well, matching the (household_id, source_ref,
        // source_line_ref) durable index.
        //
        // Shortfall recompute (plantry-fks fix 1): instead of returning 0, compute the shortfall the
        // original consume recorded — i.e. what was REQUESTED minus the sum of the journal rows that
        // came from that consume (summed in the request unit, which is unitId here). This preserves
        // the real partial shortfall when ReconcilePendingCooks re-drives a line whose consume
        // already committed.
        if (sourceLineRef is { } token)
        {
            var priorRows = _journal
                .Where(j => j.SourceLineRef == token && j.SourceRef == sourceRef)
                .ToList();
            if (priorRows.Count > 0)
            {
                // Each prior row's Delta is in the lot's unit; convert back to the request unit to
                // reconstruct what was actually deducted. The sum of Abs(Delta) in request-unit
                // terms is the satisfied portion; shortfall = amount − satisfied.
                // If conversion is unavailable for any row the prior rows must have been in the
                // same unit (same-unit consume), so we fall back to summing the deltas directly.
                // Either way we never over-estimate the shortfall.
                var satisfied = 0m;
                foreach (var row in priorRows)
                {
                    var converted = converter.Convert(Math.Abs(row.Delta), row.UnitId, unitId);
                    satisfied += converted.IsSuccess ? converted.Value : Math.Abs(row.Delta);
                }
                var recomputedShortfall = Math.Max(0m, amount - satisfied);
                return new ConsumeOutcome([], recomputedShortfall, unitId);
            }
        }

        // Build the candidate set: named lot (targeted consume) → location-scoped FEFO → global FEFO.
        IEnumerable<StockEntry> activeLots = _entries.Where(e => e.IsActive);
        if (locationId is { } loc)
            activeLots = activeLots.Where(e => e.LocationId == loc);

        var candidates = targetEntry is { } target
            ? activeLots.Where(e => e.Id == target).ToList()
            : OrderFefo(activeLots).ToList();

        if (targetEntry is { } missing && candidates.Count == 0)
            return Error.Custom("Inventory.LotNotFound", $"No active lot '{missing}' to consume from.");

        // Planning pass — resolve conversions and decide the per-lot split without mutating anything.
        var plan = new List<(StockEntry Lot, decimal TakeInLotUnit, decimal TakeInRequestUnit)>();
        var remaining = amount; // in the requested unit
        foreach (var lot in candidates)
        {
            if (remaining <= 0m) break;

            var neededInLot = converter.Convert(remaining, unitId, lot.UnitId);
            if (neededInLot.IsFailure) return neededInLot.Error;

            var takeInLot = Math.Min(lot.Quantity, neededInLot.Value);

            var takeInRequest = converter.Convert(takeInLot, lot.UnitId, unitId);
            if (takeInRequest.IsFailure) return takeInRequest.Error;

            plan.Add((lot, takeInLot, takeInRequest.Value));
            remaining -= takeInRequest.Value;
        }

        // Apply pass — mutate lots and append journal rows.
        var now = clock.UtcNow;
        var deductions = new List<LotDeduction>(plan.Count);
        foreach (var (lot, takeInLot, _) in plan)
        {
            lot.Deduct(takeInLot, clock);
            _journal.Add(StockJournalEntry.Record(
                HouseholdId, ProductId, lot.Id, -takeInLot, lot.UnitId,
                reason, sourceType, sourceRef, sourceLineRef, now, userId));
            deductions.Add(new LotDeduction(lot.Id, takeInLot, lot.UnitId));
        }

        if (plan.Count > 0) UpdatedAt = now;

        var shortfall = remaining > 0m ? remaining : 0m;
        return new ConsumeOutcome(deductions, shortfall, unitId);
    }

    // Identity is the composite (household_id, product_id); EF maps those columns as the key and
    // does not populate the base Id on materialization, so compare on the real key properties.
    public override bool Equals(object? obj) =>
        obj is ProductStock other && HouseholdId == other.HouseholdId && ProductId == other.ProductId;

    public override int GetHashCode() => HashCode.Combine(HouseholdId, ProductId);
}
