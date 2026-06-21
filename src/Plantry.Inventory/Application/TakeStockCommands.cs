using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Inventory.Application;

// ─── Result types ─────────────────────────────────────────────────────────────

/// <summary>
/// The direction of the reconciling delta applied by <see cref="RecordCountCommand"/>.
/// <c>Up</c> means stock was added (counted &gt; recorded); <c>Down</c> means stock was consumed
/// (counted &lt; recorded); <c>NoOp</c> means the counted value matched the recorded value and no
/// journal row was written (TS-7 idempotency: a re-drive of an already-applied item yields NoOp).
/// </summary>
public enum CountDirection { Up, Down, NoOp }

/// <summary>
/// The outcome returned by <see cref="RecordCountCommand"/>. Reports the direction taken and the
/// magnitude of the applied delta (in the counted unit). On <see cref="CountDirection.Down"/>
/// when stock is insufficient, <see cref="Shortfall"/> is positive (the demand that could not be
/// satisfied from in-Location lots — the consume did not over-deduct).
/// </summary>
public sealed record RecordCountOutcome(
    CountDirection Direction,
    decimal AppliedDelta,
    decimal Shortfall);

/// <summary>
/// The per-item result within a <see cref="SaveCountsCommand"/> batch. Either succeeded with a
/// <see cref="Outcome"/> or failed with an <see cref="Error"/>. The UI uses this to surface
/// per-row success/failure so the user can retry individual failures without re-saving the rest.
/// </summary>
public sealed record CountItemResult(
    Guid ProductId,
    Guid LocationId,
    bool IsSuccess,
    RecordCountOutcome? Outcome,
    Error? FailureReason)
{
    public static CountItemResult Ok(Guid productId, Guid locationId, RecordCountOutcome outcome) =>
        new(productId, locationId, true, outcome, null);

    public static CountItemResult Fail(Guid productId, Guid locationId, Error error) =>
        new(productId, locationId, false, null, error);
}

// ─── Scalar count item (P4-4a; escape-hatch payload is P4-5) ─────────────────

/// <summary>
/// One item in a <see cref="SaveCountsCommand"/> batch — the scalar set-to-N form.
/// The escape-hatch payload (per-lot adjustments) is P4-5 and extends this type.
/// </summary>
public sealed record CountItem(
    Guid ProductId,
    Guid LocationId,
    decimal CountedValue,
    Guid CountedUnitId,
    StockReason Reason = StockReason.Correction);

// ─── Lot escape-hatch (P4-5) ──────────────────────────────────────────────────

/// <summary>
/// One per-lot adjustment in a <see cref="SaveLotAdjustmentsCommand"/> batch (P4-5, J3).
/// Two shapes:
/// <list type="bullet">
/// <item><b>Reduce</b> — targeted <see cref="ProductStock.Consume"/> on an existing lot identified
/// by <see cref="EntryId"/>. <see cref="Amount"/> and <see cref="UnitId"/> are the quantity to
/// remove in the given unit. <see cref="Reason"/> must be a removal reason; use
/// <see cref="StockReason.Discarded"/> for the "spoiled" toggle (TS-4 / C9).</item>
/// <item><b>FoundStock</b> — <see cref="ProductStock.AddStock"/> with
/// <see cref="StockReason.Correction"/> (upward Correction per TS-4). <see cref="EntryId"/> is
/// null; <see cref="ExpiryDate"/> is the optional expiry supplied by the user.</item>
/// </list>
/// Both shapes target one (product, location) pair and run inside the same transaction as the
/// <see cref="SaveLotAdjustmentsCommand"/> for that product.
/// </summary>
public sealed record LotAdjustItem(
    /// <summary>The lot to reduce (null for found-stock additions).</summary>
    Guid? EntryId,
    /// <summary>
    /// Amount to remove (Reduce) or add (FoundStock). Must be positive; the direction is inferred
    /// from whether <see cref="EntryId"/> is null.
    /// </summary>
    decimal Amount,
    Guid UnitId,
    /// <summary>
    /// Removal reason for the Reduce shape (ignored for FoundStock). Defaults to
    /// <see cref="StockReason.Correction"/> (inventory discrepancy fix). Pass
    /// <see cref="StockReason.Discarded"/> for the spoiled toggle.
    /// </summary>
    StockReason Reason = StockReason.Correction,
    /// <summary>Optional expiry date for found-stock additions (ignored for Reduce).</summary>
    DateOnly? ExpiryDate = null)
{
    /// <summary>True when this is a found-stock addition (no target lot); false for a lot reduce.</summary>
    public bool IsFoundStock => EntryId is null;
}

/// <summary>
/// The outcome returned for one <see cref="LotAdjustItem"/> within a
/// <see cref="SaveLotAdjustmentsCommand"/>. Either succeeded or failed with a reason.
/// </summary>
public sealed record LotAdjustResult(
    Guid? EntryId,
    bool IsSuccess,
    Error? FailureReason)
{
    public static LotAdjustResult Ok(Guid? entryId) => new(entryId, true, null);
    public static LotAdjustResult Fail(Guid? entryId, Error error) => new(entryId, false, error);
}

/// <summary>
/// The outcome of a <see cref="SaveLotAdjustmentsCommand"/> execution — whether it succeeded at
/// the product level, plus per-adjustment results for the UI to surface inline.
/// </summary>
public sealed record SaveLotAdjustmentsOutcome(
    bool IsSuccess,
    IReadOnlyList<LotAdjustResult> Results,
    Error? FailureReason = null)
{
    public static SaveLotAdjustmentsOutcome Ok(IReadOnlyList<LotAdjustResult> results) =>
        new(true, results);

    public static SaveLotAdjustmentsOutcome Fail(Error error) =>
        new(false, [], error);
}

/// <summary>
/// Applies a set of per-lot adjustments to one product's stock in one Location (P4-5, J3).
/// Runs inside a single <c>FOR UPDATE</c> transaction so all lot mutations are atomic per product.
///
/// Each <see cref="LotAdjustItem"/> is either:
/// <list type="bullet">
/// <item>A <b>Reduce</b> — targeted <see cref="ProductStock.Consume"/> on the named lot
/// (<see cref="LotAdjustItem.EntryId"/> set). Reason: Correction / Consumed / Discarded (C9).</item>
/// <item>A <b>FoundStock</b> addition — <see cref="ProductStock.AddStock"/> with
/// <see cref="StockReason.Correction"/> and the user-supplied optional expiry (TS-4).</item>
/// </list>
///
/// Items are processed in order; a single item failure is recorded in the result vector but does
/// not abort the remaining items. The transaction is committed after all items are processed.
/// <c>source_type = Manual</c>.
/// </summary>
public sealed class SaveLotAdjustmentsCommand(
    Guid productId,
    Guid locationId,
    IReadOnlyList<LotAdjustItem> adjustments,
    Guid userId,
    IProductStockRepository stocks,
    IProductConversionProvider conversions,
    IClock clock,
    ITenantContext tenant)
{
    public async Task<Result<SaveLotAdjustmentsOutcome>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        if (adjustments.Count == 0)
            return SaveLotAdjustmentsOutcome.Ok([]);

        var converter = await conversions.ForProductAsync(productId, ct);
        var household = HouseholdId.From(householdId);

        return await stocks.ExecuteInTransactionAsync(async innerCt =>
        {
            var stock = await stocks.FindForUpdateAsync(household, productId, innerCt);
            if (stock is null)
                return Result<SaveLotAdjustmentsOutcome>.Success(
                    SaveLotAdjustmentsOutcome.Fail(
                        Error.Custom("Inventory.NoStock", "There is no stock for this product.")));

            var results = new List<LotAdjustResult>(adjustments.Count);

            foreach (var item in adjustments)
            {
                if (item.IsFoundStock)
                {
                    // FoundStock: AddStock with Correction reason and optional expiry (TS-4).
                    if (item.Amount <= 0m)
                    {
                        results.Add(LotAdjustResult.Fail(null,
                            Error.Custom("Inventory.InvalidLotAmount", "Found-stock quantity must be positive.")));
                        continue;
                    }

                    stock.AddStock(
                        item.Amount, item.UnitId, locationId, userId, clock,
                        expiryDate: item.ExpiryDate,
                        sourceType: StockSourceType.Manual,
                        reason: StockReason.Correction);

                    results.Add(LotAdjustResult.Ok(null));
                }
                else
                {
                    // Reduce: targeted Consume on the named lot (C9).
                    if (item.Amount <= 0m)
                    {
                        results.Add(LotAdjustResult.Fail(item.EntryId,
                            Error.Custom("Inventory.InvalidLotAmount", "Reduce quantity must be positive.")));
                        continue;
                    }

                    var consumeReason = item.Reason.IsRemoval() ? item.Reason : StockReason.Correction;
                    var outcome = stock.Consume(
                        item.Amount, item.UnitId, consumeReason, converter, userId, clock,
                        sourceType: StockSourceType.Manual,
                        targetEntry: StockEntryId.From(item.EntryId!.Value));

                    if (outcome.IsFailure)
                    {
                        results.Add(LotAdjustResult.Fail(item.EntryId, outcome.Error));
                        continue;
                    }

                    results.Add(LotAdjustResult.Ok(item.EntryId));
                }
            }

            await stocks.SaveChangesAsync(innerCt);
            return Result<SaveLotAdjustmentsOutcome>.Success(SaveLotAdjustmentsOutcome.Ok(results));
        }, ct);
    }
}

// ─── RecordCountCommand ───────────────────────────────────────────────────────

/// <summary>
/// The per-item reconcile command for a Take Stock walk (P4-4a, J2/J4). Loads the
/// <see cref="ProductStock"/> root <c>FOR UPDATE</c>, recomputes the recorded sum at
/// <paramref name="locationId"/> in the counted unit (TS-5), computes the
/// <c>delta = countedValue − recorded</c>, and dispatches <see cref="ProductStock.AddStock"/>
/// (positive delta / <see cref="CountDirection.Up"/>), <see cref="ProductStock.Consume"/>
/// (negative delta / <see cref="CountDirection.Down"/>), or a no-op
/// (<see cref="CountDirection.NoOp"/>). One aggregate, one transaction (TS-6).
///
/// Idempotent by construction (TS-7): re-driving with the same counted value recomputes
/// <c>recorded</c> against current stock, so an already-applied item yields <c>delta == 0</c>
/// (NoOp). No <c>sourceLineRef</c> token needed.
///
/// <c>source_type = Manual</c>. Depends on P4-1 (<see cref="ProductStock.AddStock"/> reason +
/// <see cref="ProductStock.Consume"/> locationId filter).
/// </summary>
public sealed class RecordCountCommand(
    Guid productId,
    Guid locationId,
    decimal countedValue,
    Guid countedUnitId,
    StockReason reason,
    Guid userId,
    IProductStockRepository stocks,
    IProductConversionProvider conversions,
    IClock clock,
    ITenantContext tenant)
{
    public async Task<Result<RecordCountOutcome>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        if (countedValue < 0m)
            return Error.Custom("Inventory.InvalidCountedValue", "Counted value must be zero or positive.");

        // Reason must be a removal reason when the delta goes down (Correction/Consumed/Discarded)
        // OR Correction when the delta goes up. We validate up-front that it is not Purchase,
        // because Purchase is not a valid reconcile reason in any direction.
        if (reason == StockReason.Purchase)
            return Error.Custom("Inventory.InvalidCountReason", "A Take Stock count cannot use the Purchase reason; use Correction, Consumed, or Discarded.");

        var converter = await conversions.ForProductAsync(productId, ct);
        var household = HouseholdId.From(householdId);

        return await stocks.ExecuteInTransactionAsync(async innerCt =>
        {
            var stock = await stocks.FindForUpdateAsync(household, productId, innerCt);

            // First-ever stock for this product — recorded is 0. Any positive count mints the root
            // with a Correction lot regardless of the caller's reason (Up is always Correction; see
            // ApplyDeltaAsync for the same invariant on the existing-stock path). A zero count is a
            // NoOp (nothing to reconcile; no root created).
            if (stock is null)
            {
                if (countedValue == 0m)
                    return new RecordCountOutcome(CountDirection.NoOp, 0m, 0m);

                stock = ProductStock.Start(household, productId, clock);
                stock.AddStock(
                    countedValue, countedUnitId, locationId, userId, clock,
                    sourceType: StockSourceType.Manual, reason: StockReason.Correction);

                if (!await stocks.TryAddAndSaveAsync(stock, innerCt))
                {
                    // Concurrent first-ever-stock race — another request won the insert.
                    // Reload under the FOR UPDATE lock and fall through to the delta path.
                    stock = (await stocks.FindForUpdateAsync(household, productId, innerCt))!;
                    return await ApplyDeltaAsync(stock, converter, innerCt);
                }

                return new RecordCountOutcome(CountDirection.Up, countedValue, 0m);
            }

            return await ApplyDeltaAsync(stock, converter, innerCt);
        }, ct);
    }

    private async Task<Result<RecordCountOutcome>> ApplyDeltaAsync(
        ProductStock stock, IQuantityConverter converter, CancellationToken ct)
    {
        // TS-5: compute the recorded sum at locationId in the counted unit.
        var recorded = 0m;
        foreach (var lot in stock.Entries.Where(e => e.IsActive && e.LocationId == locationId))
        {
            var inCounted = converter.Convert(lot.Quantity, lot.UnitId, countedUnitId);
            if (inCounted.IsFailure)
                return inCounted.Error;
            recorded += inCounted.Value;
        }

        var delta = countedValue - recorded;

        if (delta == 0m)
            return new RecordCountOutcome(CountDirection.NoOp, 0m, 0m);

        if (delta > 0m)
        {
            // Up — add a new opening-balance lot (TS-2/TS-4). Correction is the only valid
            // addition reason for Take Stock; ignore the caller's reason (which may be
            // Consumed/Discarded) because those are removal reasons only.
            stock.AddStock(
                delta, countedUnitId, locationId, userId, clock,
                sourceType: StockSourceType.Manual, reason: StockReason.Correction);
            await stocks.SaveChangesAsync(ct);
            return new RecordCountOutcome(CountDirection.Up, delta, 0m);
        }
        else
        {
            // Down — consume the absolute delta, location-scoped FEFO (TS-3).
            var consumeAmount = Math.Abs(delta);
            // Removal reason: use caller's reason (Correction/Consumed/Discarded).
            // If the caller passed a non-removal reason, fall back to Correction.
            var consumeReason = reason.IsRemoval() ? reason : StockReason.Correction;

            var outcome = stock.Consume(
                consumeAmount, countedUnitId, consumeReason, converter, userId, clock,
                sourceType: StockSourceType.Manual, locationId: locationId);

            if (outcome.IsFailure)
                return outcome.Error;

            await stocks.SaveChangesAsync(ct);
            return new RecordCountOutcome(CountDirection.Down, consumeAmount, outcome.Value.ShortfallAmount);
        }
    }
}

// ─── SaveCountsCommand ────────────────────────────────────────────────────────

/// <summary>
/// Batch orchestration for a Take Stock save (P4-4a, J4). Runs one
/// <see cref="RecordCountCommand"/> per item in its own independent transaction (TS-6) and
/// returns a per-item result vector. One item's failure does not roll back the others — each
/// committed count is independent truth. The UI uses the vector to surface per-row
/// success/failure for selective retry.
/// </summary>
public sealed class SaveCountsCommand(
    IReadOnlyList<CountItem> items,
    Guid userId,
    IProductStockRepository stocks,
    IProductConversionProvider conversions,
    IClock clock,
    ITenantContext tenant)
{
    public async Task<Result<IReadOnlyList<CountItemResult>>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Error.Unauthorized;

        var results = new List<CountItemResult>(items.Count);

        foreach (var item in items)
        {
            var cmd = new RecordCountCommand(
                item.ProductId,
                item.LocationId,
                item.CountedValue,
                item.CountedUnitId,
                item.Reason,
                userId,
                stocks,
                conversions,
                clock,
                tenant);

            var result = await cmd.ExecuteAsync(ct);

            results.Add(result.IsSuccess
                ? CountItemResult.Ok(item.ProductId, item.LocationId, result.Value)
                : CountItemResult.Fail(item.ProductId, item.LocationId, result.Error));
        }

        return Result<IReadOnlyList<CountItemResult>>.Success(results);
    }
}
