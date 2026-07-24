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
/// flows through <see cref="AddStock"/>. <see cref="MarkOpened"/>/<see cref="UnmarkOpened"/>
/// (plantry-1le6) are the "opened" transitions; <see cref="Transfer"/> (plantry-6owm) is the
/// location/freeze/thaw transition.
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
    ///
    /// <para><b>Auto-open (plantry-1le6 rule 5):</b> every lot this call deducts from without fully
    /// depleting — i.e. a partial deduction — is flipped open if it was still sealed, applying the
    /// same clamp <see cref="MarkOpened"/> uses (<paramref name="dueDaysAfterOpening"/> resolved by
    /// the caller, since Inventory must not reach into Catalog). A lot deducted to zero is left
    /// alone (nothing left to expire); an already-open lot is left alone (no re-fire). This runs for
    /// every caller of this single primitive — manual consume, Cook, Meal Plan Eat, Take Stock's
    /// targeted reduce — not just the UI's Consume sheet, per ADR-011's "one primitive" discipline.
    /// Reported back via <see cref="ConsumeOutcome.AutoOpened"/>.</para>
    /// </summary>
    public Result<ConsumeOutcome> Consume(
        decimal amount, Guid unitId, StockReason reason, IQuantityConverter converter,
        Guid userId, IClock clock, Guid? sourceRef = null, StockSourceType? sourceType = null,
        StockEntryId? targetEntry = null, Guid? sourceLineRef = null, Guid? locationId = null,
        int? dueDaysAfterOpening = null)
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
                return new ConsumeOutcome([], recomputedShortfall, unitId, []);
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
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var deductions = new List<LotDeduction>(plan.Count);
        var autoOpened = new List<MarkOpenedOutcome>();
        foreach (var (lot, takeInLot, _) in plan)
        {
            var wasSealed = !lot.IsOpen;
            lot.Deduct(takeInLot, clock);
            _journal.Add(StockJournalEntry.Record(
                HouseholdId, ProductId, lot.Id, -takeInLot, lot.UnitId,
                reason, sourceType, sourceRef, sourceLineRef, now, userId));
            deductions.Add(new LotDeduction(lot.Id, takeInLot, lot.UnitId));

            // Rule 5: a partial deduction from a still-sealed lot auto-opens it; a lot deducted to
            // zero is skipped (nothing left to expire) and an already-open lot never re-fires.
            if (wasSealed && lot.IsActive)
            {
                var newExpiry = ApplyOpeningClamp(lot.ExpiryDate, today, dueDaysAfterOpening);
                lot.MarkOpen(newExpiry, clock);
                autoOpened.Add(new MarkOpenedOutcome(lot.Id, newExpiry, DefaultApplied: dueDaysAfterOpening is not null));
            }
        }

        if (plan.Count > 0) UpdatedAt = now;

        var shortfall = remaining > 0m ? remaining : 0m;
        return new ConsumeOutcome(deductions, shortfall, unitId, autoOpened);
    }

    /// <summary>
    /// Marks a sealed lot as opened (plantry-1le6 UI spec §1) — flips <see cref="StockEntry.IsOpen"/>
    /// and recomputes its expiry anchored at today (DM-11 materialize-at-event-time), via
    /// <see cref="ApplyOpeningClamp"/>. Not consumption (rule 6): writes no journal row and changes no
    /// quantity. <paramref name="dueDaysAfterOpening"/> is the product's already-resolved after-opening
    /// default (Catalog fact resolved by the caller — Inventory must not reach into Catalog); null means
    /// no default is configured anywhere, so the flag flips but the expiry is left untouched (rule 4).
    /// </summary>
    public Result<MarkOpenedOutcome> MarkOpened(StockEntryId entryId, int? dueDaysAfterOpening, IClock clock)
    {
        var lot = _entries.FirstOrDefault(e => e.Id == entryId);
        if (lot is null)
            return Error.Custom("Inventory.LotNotFound", $"No lot '{entryId}' to mark opened.");
        if (!lot.IsActive)
            return Error.Custom("Inventory.LotNotActive", "A depleted lot cannot be marked opened.");
        if (lot.IsOpen)
            return Error.Custom("Inventory.LotAlreadyOpen", "This lot is already marked opened.");

        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var newExpiry = ApplyOpeningClamp(lot.ExpiryDate, today, dueDaysAfterOpening);
        lot.MarkOpen(newExpiry, clock);
        UpdatedAt = clock.UtcNow;
        return new MarkOpenedOutcome(lot.Id, newExpiry, DefaultApplied: dueDaysAfterOpening is not null);
    }

    /// <summary>
    /// Un-marks an opened lot (plantry-1le6 UI spec §3 — corrections happen). A pure flag flip: the
    /// expiry that opening replaced is <b>not</b> restored (no history is kept), and no recompute runs.
    /// </summary>
    public Result<UnmarkOpenedOutcome> UnmarkOpened(StockEntryId entryId, IClock clock)
    {
        var lot = _entries.FirstOrDefault(e => e.Id == entryId);
        if (lot is null)
            return Error.Custom("Inventory.LotNotFound", $"No lot '{entryId}' to unmark.");
        if (!lot.IsOpen)
            return Error.Custom("Inventory.LotNotOpen", "This lot is not marked opened.");

        lot.UnmarkOpen(clock);
        UpdatedAt = clock.UtcNow;
        return new UnmarkOpenedOutcome(lot.Id, lot.ExpiryDate);
    }

    /// <summary>
    /// The DM-11 opening clamp (plantry-1le6 rule 2) shared by <see cref="MarkOpened"/> and
    /// <see cref="Consume"/>'s auto-open step: opening never <b>extends</b> a printed date — the new
    /// expiry is <c>min(existingExpiry, today + dueDaysAfterOpening)</c>, deliberately the opposite of
    /// freezing's replace-outright rule (plantry-6owm), because "use within N days of opening" can
    /// never push a date later than what's already printed. A null <paramref name="existingExpiry"/>
    /// takes the candidate outright (nothing to clamp against). A null
    /// <paramref name="dueDaysAfterOpening"/> (rule 4 — no default configured anywhere) leaves
    /// <paramref name="existingExpiry"/> untouched.
    /// </summary>
    private static DateOnly? ApplyOpeningClamp(DateOnly? existingExpiry, DateOnly today, int? dueDaysAfterOpening)
    {
        if (dueDaysAfterOpening is not { } days) return existingExpiry;
        var candidate = today.AddDays(days);
        return existingExpiry is { } existing && existing < candidate ? existing : candidate;
    }

    /// <summary>
    /// Moves (all or part of) a lot to a new location (plantry-6owm) — the transfer/freeze/thaw
    /// primitive. The transition kind is derived implicitly from <paramref name="sourceIsFrozen"/> and
    /// <paramref name="destinationIsFrozen"/> (rule 2): non-frozen→frozen freezes (expiry recomputed
    /// via <paramref name="dueDaysAfterFreezing"/>), frozen→non-frozen thaws (via
    /// <paramref name="dueDaysAfterThawing"/>), same storage type either side is a plain move (expiry
    /// and timestamps untouched). Both Catalog facts are resolved by the caller — Inventory must not
    /// reach into Catalog.
    ///
    /// <para>Expiry recompute replaces outright, anchored at today (rule 3, DM-11
    /// materialize-at-event-time): <c>today + dueDays</c> — freezing may legitimately <b>extend</b> the
    /// date. A null due-days default (rule 6 — nothing configured anywhere) leaves the expiry untouched
    /// while still recording the transition timestamp.</para>
    ///
    /// <para><b>Partial transfer splits</b> (rule 1): <paramref name="quantity"/> defaults to the full
    /// lot at the call site: an exact-quantity match moves <paramref name="entryId"/> in place
    /// (<see cref="TransferOutcome.SplitEntryId"/> is null); anything less carves the moved portion
    /// into a new lot at the destination (inheriting purchase metadata/PurchasedAt) while the source
    /// lot keeps its own location and expiry, untouched, with the reduced remainder.</para>
    ///
    /// <para>Not consumption (rule 7): writes no journal row and never touches quantity-consumed
    /// accounting — product-level on-hand totals are unchanged; stock only changes location.</para>
    /// </summary>
    public Result<TransferOutcome> Transfer(
        StockEntryId entryId, Guid destinationLocationId, bool sourceIsFrozen, bool destinationIsFrozen,
        decimal quantity, IClock clock, int? dueDaysAfterFreezing, int? dueDaysAfterThawing)
    {
        var lot = _entries.FirstOrDefault(e => e.Id == entryId);
        if (lot is null)
            return Error.Custom("Inventory.LotNotFound", $"No lot '{entryId}' to move.");
        if (!lot.IsActive)
            return Error.Custom("Inventory.LotNotActive", "A depleted lot cannot be moved.");
        if (quantity <= 0m)
            return Error.Custom("Inventory.InvalidTransferQuantity", "Quantity must be greater than zero.");
        if (quantity > lot.Quantity)
            return Error.Custom("Inventory.InvalidTransferQuantity", "Cannot move more than the lot holds.");
        if (destinationLocationId == lot.LocationId)
            return Error.Custom("Inventory.SameLocation", "Choose a different location to move to.");

        var kind = !sourceIsFrozen && destinationIsFrozen ? TransferKind.Freeze
            : sourceIsFrozen && !destinationIsFrozen ? TransferKind.Thaw
            : TransferKind.Move;

        var now = clock.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var (newExpiry, defaultApplied) = kind switch
        {
            TransferKind.Freeze => (dueDaysAfterFreezing is { } f ? today.AddDays(f) : lot.ExpiryDate, dueDaysAfterFreezing is not null),
            TransferKind.Thaw => (dueDaysAfterThawing is { } t ? today.AddDays(t) : lot.ExpiryDate, dueDaysAfterThawing is not null),
            _ => (lot.ExpiryDate, false),
        };

        StockEntryId? splitEntryId = null;
        if (quantity == lot.Quantity)
        {
            lot.MoveTo(destinationLocationId, newExpiry, kind, now);
        }
        else
        {
            lot.ReduceForSplit(quantity, clock);
            var newLot = StockEntry.CreateSplit(lot, quantity, destinationLocationId, newExpiry, kind, now);
            _entries.Add(newLot);
            splitEntryId = newLot.Id;
        }

        UpdatedAt = now;
        return new TransferOutcome(lot.Id, splitEntryId, quantity, lot.UnitId, destinationLocationId, kind, newExpiry, defaultApplied);
    }

    /// <summary>
    /// Fixes a data-entry mistake on a purchase without breaking the append-only ledger (ADR-023,
    /// <c>docs/DomainDesign/purchase-entry-amendment.md</c>). Appends a compensating
    /// <see cref="StockReason.Amendment"/> journal row on the <b>same</b> lot — never a new one —
    /// and adjusts that lot's <see cref="StockEntry.Quantity"/> by the signed delta between
    /// <paramref name="correctedQuantity"/> and the lot's <b>effective purchased quantity</b> (the
    /// original Purchase row + every prior Amendment row on this lot, all in the lot's unit — the
    /// committed unit IS the lot unit, so no conversion is involved, unlike <see cref="Consume"/>).
    ///
    /// Guards (spec §3/A4, evaluated in this order):
    /// <list type="number">
    /// <item><paramref name="correctedQuantity"/> must be positive.</item>
    /// <item>It must be at least the total already consumed from this lot (Σ of every negative
    /// <see cref="StockJournalEntry.Delta"/> recorded against this <see cref="StockEntryId"/>, any
    /// reason) — an amendment can never drive the lot negative
    /// (<c>Inventory.AmendBelowConsumed</c>).</item>
    /// <item>The lot must be active (not depleted) — resurrecting a depleted lot is out of scope
    /// for v1.</item>
    /// <item>The amendment must not be closed: closed once any <see cref="StockReason.Correction"/>
    /// row exists anywhere on this <b>product</b> (not just this lot — A5, deliberately
    /// conservative: a Take Stock positive true-up lands as a new lot, invisible lot-locally) dated
    /// after this lot's Purchase row (<c>Inventory.AmendmentClosedByCorrection</c>).</item>
    /// </list>
    ///
    /// A zero delta (the corrected quantity already matches the effective quantity) is a no-op
    /// success — the idempotent-re-drive guarantee for <c>AmendCommittedLineCommand</c>'s retry
    /// after a mid-sequence failure (spec A10). Returns the signed delta actually applied, for the
    /// caller's toast/UI ("+2 lb" / "−0.5 lb").
    /// </summary>
    public Result<decimal> AmendPurchase(
        StockEntryId entryId, decimal correctedQuantity, Guid importLineId, Guid userId, IClock clock)
    {
        if (correctedQuantity <= 0m)
            return Error.Custom("Inventory.InvalidAmendQuantity", "Corrected quantity must be greater than zero.");

        var lot = _entries.FirstOrDefault(e => e.Id == entryId);
        if (lot is null)
            return Error.Custom("Inventory.LotNotFound", $"No lot '{entryId}' to amend.");

        var purchaseRow = _journal
            .Where(j => j.StockEntryId == entryId && j.Reason == StockReason.Purchase)
            .OrderBy(j => j.OccurredAt)
            .FirstOrDefault();
        if (purchaseRow is null)
            return Error.Custom("Inventory.LotNotFromIntake", "This lot was not created by an intake purchase and cannot be amended.");

        // Only TRUE consumption counts here — Consumed/Discarded/negative-Correction removals.
        // A prior downward Amendment is a correction to the purchased quantity, not consumption,
        // so it must be excluded or a repeat downward amendment (A3 explicitly allows repeats)
        // would be wrongly rejected against its own prior compensating delta.
        var consumedTotal = _journal
            .Where(j => j.StockEntryId == entryId && j.Reason != StockReason.Amendment && j.Delta < 0m)
            .Sum(j => -j.Delta);
        if (correctedQuantity < consumedTotal)
            return Error.Custom(
                "Inventory.AmendBelowConsumed",
                $"Corrected quantity cannot be less than the {consumedTotal} already consumed from this lot.");

        if (!lot.IsActive)
            return Error.Custom("Inventory.LotNotActive", "A depleted lot cannot be amended.");

        var closedByCorrection = _journal.Any(j =>
            j.Reason == StockReason.Correction && j.OccurredAt > purchaseRow.OccurredAt);
        if (closedByCorrection)
            return Error.Custom(
                "Inventory.AmendmentClosedByCorrection",
                "This product has been recounted since the purchase; the amendment window is closed. Use a recount (Take Stock) instead.");

        var priorAmendments = _journal
            .Where(j => j.StockEntryId == entryId && j.Reason == StockReason.Amendment)
            .Sum(j => j.Delta);
        var effectiveQuantity = purchaseRow.Delta + priorAmendments;
        var delta = correctedQuantity - effectiveQuantity;
        if (delta == 0m)
            return delta; // A10: idempotent re-drive — already at the corrected quantity, nothing to do.

        var now = clock.UtcNow;
        if (delta > 0m)
            lot.Increase(delta, clock);
        else
            lot.Deduct(-delta, clock);

        _journal.Add(StockJournalEntry.Record(
            HouseholdId, ProductId, lot.Id, delta, lot.UnitId,
            StockReason.Amendment, StockSourceType.Intake, importLineId, sourceLineRef: null, now, userId));
        UpdatedAt = now;

        return delta;
    }

    // Identity is the composite (household_id, product_id); EF maps those columns as the key and
    // does not populate the base Id on materialization, so compare on the real key properties.
    public override bool Equals(object? obj) =>
        obj is ProductStock other && HouseholdId == other.HouseholdId && ProductId == other.ProductId;

    public override int GetHashCode() => HashCode.Combine(HouseholdId, ProductId);
}
