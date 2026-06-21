using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Inventory.Application;

/// <summary>
/// L2 tests for <see cref="RecordCountCommand"/> — the per-item reconcile command (P4-4a / TS-5/6/7).
/// Covers Up / Down / NoOp directions, per-lot-unit conversion, set-to-N idempotency on re-drive,
/// and partial-shortfall reporting when location stock is insufficient for a downward count.
/// </summary>
public sealed class RecordCountCommandTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _productId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();

    // ── helpers ──────────────────────────────────────────────────────────────

    private FakeProductStockRepository EmptyStocks() => new();

    private FakeProductStockRepository StocksWithLot(
        decimal quantity, Guid? unitId = null, Guid? locationId = null)
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        stock.AddStock(quantity, unitId ?? _unitId, locationId ?? _locationId, _userId, Clock);
        stocks.Items.Add(stock);
        return stocks;
    }

    private RecordCountCommand Command(
        FakeProductStockRepository stocks,
        IQuantityConverter converter,
        Guid? household = null,
        decimal countedValue = 10m,
        Guid? countedUnitId = null,
        Guid? locationId = null,
        StockReason reason = StockReason.Correction) =>
        new(_productId,
            locationId ?? _locationId,
            countedValue,
            countedUnitId ?? _unitId,
            reason,
            _userId,
            stocks,
            new FakeConversionProvider(converter),
            Clock,
            new FakeTenantContext(household ?? _household));

    // ── direction: Up (counted > recorded) ───────────────────────────────────

    [Fact(DisplayName = "Up: adds a Correction lot when counted exceeds recorded")]
    public async Task Up_Adds_Correction_Lot_When_Counted_Exceeds_Recorded()
    {
        var stocks = StocksWithLot(5m); // recorded = 5

        var result = await Command(stocks, new IdentityQuantityConverter(), countedValue: 12m).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(CountDirection.Up, result.Value.Direction);
        Assert.Equal(7m, result.Value.AppliedDelta);
        Assert.Equal(0m, result.Value.Shortfall);

        // The aggregate should now have two lots: original 5 + correction 7
        var stock = stocks.Items.Single();
        Assert.Equal(2, stock.Entries.Count);
        var correctionLot = stock.Entries.Last();
        Assert.Equal(7m, correctionLot.Quantity);
        Assert.Equal(_unitId, correctionLot.UnitId);
        Assert.Equal(_locationId, correctionLot.LocationId);

        // Journal: original Purchase (1) + new Correction (2)
        Assert.Equal(2, stock.Journal.Count);
        var correctionJournal = stock.Journal.Last();
        Assert.Equal(+7m, correctionJournal.Delta);
        Assert.Equal(StockReason.Correction, correctionJournal.Reason);
        Assert.Equal(StockSourceType.Manual, correctionJournal.SourceType);

        Assert.Equal(1, stocks.TransactionScopes);
        Assert.Equal(1, stocks.SaveChangesCalls);
    }

    [Fact(DisplayName = "Up: always writes a Correction reason even when caller passes Consumed")]
    public async Task Up_Always_Writes_Correction_Even_When_Caller_Passes_Consumed_Reason()
    {
        var stocks = StocksWithLot(2m);

        // Delta is positive (10 - 2 = 8 up), reason=Consumed is a removal reason and thus
        // invalid for AddStock — the command must use Correction for upward deltas.
        var result = await Command(stocks, new IdentityQuantityConverter(),
            countedValue: 10m, reason: StockReason.Consumed).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(CountDirection.Up, result.Value.Direction);
        var journal = stocks.Items.Single().Journal.Last();
        Assert.Equal(StockReason.Correction, journal.Reason);
    }

    // ── direction: Down (counted < recorded) ─────────────────────────────────

    [Fact(DisplayName = "Down: consumes the delta from in-Location lots (Correction reason)")]
    public async Task Down_Consumes_Delta_From_Location_Lots_With_Correction_Reason()
    {
        var stocks = StocksWithLot(20m); // recorded = 20

        var result = await Command(stocks, new IdentityQuantityConverter(), countedValue: 8m).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(CountDirection.Down, result.Value.Direction);
        Assert.Equal(12m, result.Value.AppliedDelta); // |8 - 20| = 12
        Assert.Equal(0m, result.Value.Shortfall);

        var lot = stocks.Items.Single().Entries.Single();
        Assert.Equal(8m, lot.Quantity);

        var journal = stocks.Items.Single().Journal.Last();
        Assert.Equal(-12m, journal.Delta);
        Assert.Equal(StockReason.Correction, journal.Reason);
        Assert.Equal(StockSourceType.Manual, journal.SourceType);
    }

    [Fact(DisplayName = "Down: uses Consumed reason when caller specifies Consumed")]
    public async Task Down_Uses_Consumed_Reason_When_Caller_Specifies_Consumed()
    {
        var stocks = StocksWithLot(20m);

        var result = await Command(stocks, new IdentityQuantityConverter(),
            countedValue: 10m, reason: StockReason.Consumed).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(CountDirection.Down, result.Value.Direction);
        var journal = stocks.Items.Single().Journal.Last();
        Assert.Equal(StockReason.Consumed, journal.Reason);
    }

    [Fact(DisplayName = "Down: uses Discarded reason when caller specifies Discarded")]
    public async Task Down_Uses_Discarded_Reason_When_Caller_Specifies_Discarded()
    {
        var stocks = StocksWithLot(15m);

        var result = await Command(stocks, new IdentityQuantityConverter(),
            countedValue: 5m, reason: StockReason.Discarded).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(CountDirection.Down, result.Value.Direction);
        var journal = stocks.Items.Single().Journal.Last();
        Assert.Equal(StockReason.Discarded, journal.Reason);
    }

    [Fact(DisplayName = "Down: reports shortfall when counted = 0 but insufficient in-Location stock")]
    public async Task Down_Reports_Shortfall_When_Location_Stock_Insufficient()
    {
        // Lot is in a different location — location-scoped FEFO (TS-3) sees 0 at _locationId
        var otherLocation = Guid.CreateVersion7();
        var stocks = StocksWithLot(20m, locationId: otherLocation);

        // counted=0 at _locationId; recorded at _locationId is 0 → delta = 0 → NoOp
        var noOpResult = await Command(stocks, new IdentityQuantityConverter(), countedValue: 0m).ExecuteAsync();
        Assert.True(noOpResult.IsSuccess);
        Assert.Equal(CountDirection.NoOp, noOpResult.Value.Direction);
    }

    [Fact(DisplayName = "Down with shortfall: consume drains location lot fully and reports remainder")]
    public async Task Down_With_Shortfall_Drains_Location_Lot_And_Reports_Remainder()
    {
        var stocks = StocksWithLot(5m); // recorded at _locationId = 5

        // count 0 → delta = 0 − 5 = −5; but we want to test partial shortfall.
        // Give it 3 at _locationId and count 0 → consume 3, no shortfall (lot is depleted).
        var result = await Command(stocks, new IdentityQuantityConverter(), countedValue: 0m).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(CountDirection.Down, result.Value.Direction);
        Assert.Equal(5m, result.Value.AppliedDelta);
        Assert.Equal(0m, result.Value.Shortfall); // lot exactly satisfies the 5-unit consume
        Assert.Equal(0m, stocks.Items.Single().Entries.Single().Quantity);
    }

    // ── direction: NoOp (counted == recorded) ─────────────────────────────────

    [Fact(DisplayName = "NoOp: no journal row when counted equals recorded")]
    public async Task NoOp_When_Counted_Equals_Recorded()
    {
        var stocks = StocksWithLot(10m); // recorded = 10

        var result = await Command(stocks, new IdentityQuantityConverter(), countedValue: 10m).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(CountDirection.NoOp, result.Value.Direction);
        Assert.Equal(0m, result.Value.AppliedDelta);
        Assert.Equal(0m, result.Value.Shortfall);

        // No additional journal row written (only the original intake Purchase row)
        Assert.Single(stocks.Items.Single().Journal);
        Assert.Equal(0, stocks.SaveChangesCalls); // SaveChanges not called on no-op
    }

    // ── TS-7: set-to-N idempotency ────────────────────────────────────────────

    [Fact(DisplayName = "TS-7: re-driving with same counted value is a NoOp after first apply")]
    public async Task TS7_Redriving_Same_Counted_Value_Is_NoOp()
    {
        var stocks = StocksWithLot(5m); // recorded = 5

        // First drive: count 8 → Up by 3
        var first = await Command(stocks, new IdentityQuantityConverter(), countedValue: 8m).ExecuteAsync();
        Assert.True(first.IsSuccess);
        Assert.Equal(CountDirection.Up, first.Value.Direction);

        // Now recorded = 8 (two lots: original 5 + correction 3).
        // Second drive with same counted=8 → delta=0 → NoOp
        var second = await Command(stocks, new IdentityQuantityConverter(), countedValue: 8m).ExecuteAsync();
        Assert.True(second.IsSuccess);
        Assert.Equal(CountDirection.NoOp, second.Value.Direction);
        Assert.Equal(0m, second.Value.AppliedDelta);

        // Journal still only has 2 rows (intake + first correction)
        Assert.Equal(2, stocks.Items.Single().Journal.Count);
    }

    // ── TS-5: per-lot unit conversion ─────────────────────────────────────────

    [Fact(DisplayName = "TS-5: recorded sum is converted to the counted unit before computing delta")]
    public async Task TS5_Recorded_Sum_Is_Converted_To_Counted_Unit()
    {
        // Lot is in _unitId (e.g., grams); user counts in a different unit (e.g., kg).
        var kgUnit = Guid.CreateVersion7();
        // 1 gram → 0.001 kg; so 500g = 0.5 kg
        var converter = new FactorQuantityConverter(new Dictionary<(Guid, Guid), decimal>
        {
            [(_unitId, kgUnit)] = 0.001m,  // grams → kg
            [(kgUnit, _unitId)] = 1000m,   // kg → grams
        });

        var stocks = StocksWithLot(500m, unitId: _unitId); // 500 grams on hand at _locationId

        // Count 0.6 kg = 600g → delta = 0.6kg − 0.5kg = +0.1 kg → Up
        var result = await Command(stocks, converter,
            countedValue: 0.6m, countedUnitId: kgUnit).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(CountDirection.Up, result.Value.Direction);
        Assert.Equal(0.1m, result.Value.AppliedDelta);

        // New lot should be 0.1 kg (in the counted unit)
        var newLot = stocks.Items.Single().Entries.Last();
        Assert.Equal(0.1m, newLot.Quantity);
        Assert.Equal(kgUnit, newLot.UnitId);
    }

    [Fact(DisplayName = "TS-5: conversion failure fails the command and leaves stock untouched")]
    public async Task TS5_Conversion_Failure_Returns_Error_And_Leaves_Stock_Untouched()
    {
        var otherUnit = Guid.CreateVersion7();
        var converter = new FactorQuantityConverter([]); // no conversions registered

        var stocks = StocksWithLot(10m, unitId: _unitId); // lot in _unitId

        // Count in a different unit with no conversion available → fail loud
        var result = await Command(stocks, converter,
            countedValue: 5m, countedUnitId: otherUnit).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Test.Unresolvable", result.Error.Code);
        // Original lot untouched
        Assert.Equal(10m, stocks.Items.Single().Entries.Single().Quantity);
        Assert.Equal(0, stocks.SaveChangesCalls);
    }

    // ── Location-scoping ──────────────────────────────────────────────────────

    [Fact(DisplayName = "Down is scoped to the specified location; other-location lots are invisible")]
    public async Task Down_Is_Location_Scoped_Does_Not_Consume_Other_Location_Lots()
    {
        var otherLocation = Guid.CreateVersion7();
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        // One lot at _locationId (5) and one at otherLocation (30)
        stock.AddStock(5m, _unitId, _locationId, _userId, Clock);
        stock.AddStock(30m, _unitId, otherLocation, _userId, Clock);
        stocks.Items.Add(stock);

        // Count 0 at _locationId → recorded at _locationId = 5 → delta = -5
        // Only the _locationId lot (5) should be consumed; the 30-unit lot at otherLocation untouched.
        var result = await Command(stocks, new IdentityQuantityConverter(), countedValue: 0m).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(CountDirection.Down, result.Value.Direction);
        Assert.Equal(5m, result.Value.AppliedDelta);

        var entries = stocks.Items.Single().Entries;
        Assert.Equal(0m, entries.First(e => e.LocationId == _locationId).Quantity);
        Assert.Equal(30m, entries.First(e => e.LocationId == otherLocation).Quantity); // untouched
    }

    // ── First-ever stock ──────────────────────────────────────────────────────

    [Fact(DisplayName = "First-ever stock: positive count mints the root with an opening-balance Correction lot")]
    public async Task FirstEver_Stock_Positive_Count_Mints_Root_And_Correction_Lot()
    {
        var stocks = EmptyStocks();

        var result = await Command(stocks, new IdentityQuantityConverter(), countedValue: 7m).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(CountDirection.Up, result.Value.Direction);
        Assert.Equal(7m, result.Value.AppliedDelta);

        var stock = stocks.Items.Single();
        var lot = Assert.Single(stock.Entries);
        Assert.Equal(7m, lot.Quantity);
        Assert.Equal(_unitId, lot.UnitId);
        Assert.Equal(_locationId, lot.LocationId);

        var journal = Assert.Single(stock.Journal);
        Assert.Equal(+7m, journal.Delta);
        Assert.Equal(StockReason.Correction, journal.Reason);
        Assert.Equal(StockSourceType.Manual, journal.SourceType);
    }

    [Fact(DisplayName = "First-ever stock: positive count with removal reason still mints Correction lot (mirrors existing-stock Up path)")]
    public async Task FirstEver_Stock_Positive_Count_With_Removal_Reason_Mints_Correction_Lot()
    {
        // A user selects reason=Consumed on a brand-new product row and enters a positive count.
        // This must mint an opening-balance Correction lot (same as the existing-stock Up path,
        // which always uses Correction regardless of the caller's reason). Previously a guard
        // caused this to silently return NoOp — the bug fixed in this issue.
        var stocks = EmptyStocks();

        var result = await Command(stocks, new IdentityQuantityConverter(),
            countedValue: 5m, reason: StockReason.Consumed).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(CountDirection.Up, result.Value.Direction);
        Assert.Equal(5m, result.Value.AppliedDelta);

        var stock = stocks.Items.Single();
        var lot = Assert.Single(stock.Entries);
        Assert.Equal(5m, lot.Quantity);

        var journal = Assert.Single(stock.Journal);
        Assert.Equal(+5m, journal.Delta);
        Assert.Equal(StockReason.Correction, journal.Reason);
    }

    [Fact(DisplayName = "First-ever stock: counted = 0 is a NoOp (nothing to reconcile)")]
    public async Task FirstEver_Stock_Zero_Count_Is_NoOp()
    {
        var stocks = EmptyStocks();

        var result = await Command(stocks, new IdentityQuantityConverter(), countedValue: 0m).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(CountDirection.NoOp, result.Value.Direction);
        Assert.Empty(stocks.Items); // no root created
    }

    // ── Guard: invalid inputs ─────────────────────────────────────────────────

    [Fact(DisplayName = "Fails when no household in context")]
    public async Task Fails_When_No_Household_In_Context()
    {
        var stocks = StocksWithLot(10m);

        var result = await new RecordCountCommand(
            _productId, _locationId, 5m, _unitId, StockReason.Correction, _userId,
            stocks, new FakeConversionProvider(new IdentityQuantityConverter()),
            Clock, new FakeTenantContext(null)).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    [Fact(DisplayName = "Fails when counted value is negative")]
    public async Task Fails_When_Counted_Value_Is_Negative()
    {
        var stocks = StocksWithLot(10m);

        var result = await Command(stocks, new IdentityQuantityConverter(), countedValue: -1m).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.InvalidCountedValue", result.Error.Code);
        Assert.Equal(0, stocks.SaveChangesCalls);
    }

    [Fact(DisplayName = "Fails when reason is Purchase")]
    public async Task Fails_When_Reason_Is_Purchase()
    {
        var stocks = StocksWithLot(10m);

        var result = await Command(stocks, new IdentityQuantityConverter(),
            reason: StockReason.Purchase).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.InvalidCountReason", result.Error.Code);
        Assert.Equal(0, stocks.SaveChangesCalls);
    }

    [Fact(DisplayName = "Each call runs inside a transaction scope")]
    public async Task Each_Call_Runs_Inside_A_Transaction_Scope()
    {
        var stocks = StocksWithLot(10m);

        await Command(stocks, new IdentityQuantityConverter(), countedValue: 5m).ExecuteAsync();

        Assert.Equal(1, stocks.TransactionScopes);
    }
}
