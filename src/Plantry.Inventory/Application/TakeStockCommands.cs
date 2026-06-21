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
