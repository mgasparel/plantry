using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Inventory.Application;

/// <summary>
/// L2 tests for <see cref="SaveLotAdjustmentsCommand"/> — the lot escape-hatch command (P4-5 / J3).
///
/// Covers:
///  - Per-lot reduce writes a lot-scoped removal with the correct reason.
///  - Spoiled flag → <see cref="StockReason.Discarded"/> journal entry.
///  - Found stock → positive Correction lot with optional expiry (TS-4).
///  - Zero-amount items are rejected with a descriptive error.
///  - Unknown lot (no active entry for the given EntryId) returns LotNotFound error.
///  - Empty adjustments list returns an empty success outcome.
///  - No stock root → returns NoStock failure inside the outcome (not a top-level error).
///  - No household context → top-level Unauthorized.
///  - All adjustments run inside a single transaction per product.
///  - Partial success: one failed item does not abort the rest (within the transaction).
/// </summary>
public sealed class SaveLotAdjustmentsCommandTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _productId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Seeds a stock root with one lot and returns both the repo and the seeded lot's EntryId.</summary>
    private (FakeProductStockRepository Stocks, Guid EntryId) StocksWithLot(
        decimal quantity, Guid? unitId = null, Guid? locationId = null)
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        var entry = stock.AddStock(quantity, unitId ?? _unitId, locationId ?? _locationId, _userId, Clock);
        stocks.Items.Add(stock);
        return (stocks, entry.Id.Value);
    }

    private SaveLotAdjustmentsCommand Command(
        FakeProductStockRepository stocks,
        IReadOnlyList<LotAdjustItem> adjustments,
        Guid? household = null) =>
        new(_productId, _locationId, adjustments, _userId, stocks,
            new FakeConversionProvider(new IdentityQuantityConverter()),
            Clock,
            new FakeTenantContext(household ?? _household));

    // ── Per-lot reduce ────────────────────────────────────────────────────────

    [Fact(DisplayName = "Reduce: removes the specified amount from the target lot")]
    public async Task Reduce_Removes_Amount_From_Target_Lot()
    {
        var (stocks, entryId) = StocksWithLot(100m);

        var adjustments = new List<LotAdjustItem>
        {
            new(entryId, 30m, _unitId, StockReason.Correction),
        };

        var result = await Command(stocks, adjustments).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsSuccess);
        Assert.Single(result.Value.Results);
        Assert.True(result.Value.Results[0].IsSuccess);
        Assert.Equal(entryId, result.Value.Results[0].EntryId);

        var lot = stocks.Items.Single().Entries.Single();
        Assert.Equal(70m, lot.Quantity);

        var journal = stocks.Items.Single().Journal.Last();
        Assert.Equal(-30m, journal.Delta);
        Assert.Equal(StockReason.Correction, journal.Reason);
        Assert.Equal(StockSourceType.Manual, journal.SourceType);
    }

    // ── Spoiled → Discarded ───────────────────────────────────────────────────

    [Fact(DisplayName = "Spoiled: Discarded reason when reason=Discarded (C9)")]
    public async Task Spoiled_Writes_Discarded_Reason()
    {
        var (stocks, entryId) = StocksWithLot(50m);

        var adjustments = new List<LotAdjustItem>
        {
            new(entryId, 20m, _unitId, StockReason.Discarded),
        };

        var result = await Command(stocks, adjustments).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsSuccess);
        Assert.True(result.Value.Results[0].IsSuccess);

        var journal = stocks.Items.Single().Journal.Last();
        Assert.Equal(-20m, journal.Delta);
        Assert.Equal(StockReason.Discarded, journal.Reason);
    }

    [Fact(DisplayName = "Reduce with Consumed reason writes Consumed journal entry")]
    public async Task Reduce_With_Consumed_Reason_Writes_Consumed_Journal_Entry()
    {
        var (stocks, entryId) = StocksWithLot(80m);

        var adjustments = new List<LotAdjustItem>
        {
            new(entryId, 15m, _unitId, StockReason.Consumed),
        };

        var result = await Command(stocks, adjustments).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Results[0].IsSuccess);

        var journal = stocks.Items.Single().Journal.Last();
        Assert.Equal(StockReason.Consumed, journal.Reason);
        Assert.Equal(-15m, journal.Delta);
    }

    // ── Found stock → positive Correction lot ─────────────────────────────────

    [Fact(DisplayName = "FoundStock: adds a positive Correction lot with the given expiry (TS-4)")]
    public async Task FoundStock_Adds_Correction_Lot_With_Expiry()
    {
        var (stocks, _) = StocksWithLot(10m);
        var expiry = new DateOnly(2026, 12, 31);

        var adjustments = new List<LotAdjustItem>
        {
            new(EntryId: null, Amount: 75m, UnitId: _unitId, ExpiryDate: expiry),
        };

        var result = await Command(stocks, adjustments).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsSuccess);
        Assert.Single(result.Value.Results);
        Assert.True(result.Value.Results[0].IsSuccess);
        Assert.Null(result.Value.Results[0].EntryId);

        // Stock should now have two entries: original lot + new Correction lot.
        var entries = stocks.Items.Single().Entries;
        Assert.Equal(2, entries.Count);
        var newLot = entries.Last();
        Assert.Equal(75m, newLot.Quantity);
        Assert.Equal(_unitId, newLot.UnitId);
        Assert.Equal(_locationId, newLot.LocationId);
        Assert.Equal(expiry, newLot.ExpiryDate);

        var journal = stocks.Items.Single().Journal.Last();
        Assert.Equal(+75m, journal.Delta);
        Assert.Equal(StockReason.Correction, journal.Reason);
        Assert.Equal(StockSourceType.Manual, journal.SourceType);
    }

    [Fact(DisplayName = "FoundStock: adds a Correction lot without expiry when expiryDate is null")]
    public async Task FoundStock_Adds_Correction_Lot_Without_Expiry()
    {
        var (stocks, _) = StocksWithLot(5m);

        var adjustments = new List<LotAdjustItem>
        {
            new(EntryId: null, Amount: 40m, UnitId: _unitId),
        };

        var result = await Command(stocks, adjustments).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Results[0].IsSuccess);

        var newLot = stocks.Items.Single().Entries.Last();
        Assert.Equal(40m, newLot.Quantity);
        Assert.Null(newLot.ExpiryDate);
    }

    // ── Validation guards ─────────────────────────────────────────────────────

    [Fact(DisplayName = "Reduce: zero amount returns an error result")]
    public async Task Reduce_ZeroAmount_Returns_Error_Result()
    {
        var (stocks, entryId) = StocksWithLot(100m);

        var adjustments = new List<LotAdjustItem>
        {
            new(entryId, 0m, _unitId),
        };

        var result = await Command(stocks, adjustments).ExecuteAsync();

        Assert.True(result.IsSuccess);
        // Command-level succeeds; the item fails.
        Assert.True(result.Value.IsSuccess);
        Assert.Single(result.Value.Results);
        Assert.False(result.Value.Results[0].IsSuccess);
        Assert.Equal("Inventory.InvalidLotAmount", result.Value.Results[0].FailureReason?.Code);
    }

    [Fact(DisplayName = "FoundStock: zero amount returns an error result")]
    public async Task FoundStock_ZeroAmount_Returns_Error_Result()
    {
        var (stocks, _) = StocksWithLot(100m);

        var adjustments = new List<LotAdjustItem>
        {
            new(EntryId: null, Amount: 0m, UnitId: _unitId),
        };

        var result = await Command(stocks, adjustments).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Results);
        Assert.False(result.Value.Results[0].IsSuccess);
        Assert.Equal("Inventory.InvalidLotAmount", result.Value.Results[0].FailureReason?.Code);
    }

    [Fact(DisplayName = "Reduce: unknown EntryId returns LotNotFound error in result")]
    public async Task Reduce_UnknownEntryId_Returns_LotNotFound_In_Result()
    {
        var (stocks, _) = StocksWithLot(100m);
        var unknownEntryId = Guid.CreateVersion7();

        var adjustments = new List<LotAdjustItem>
        {
            new(unknownEntryId, 10m, _unitId),
        };

        var result = await Command(stocks, adjustments).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Results);
        Assert.False(result.Value.Results[0].IsSuccess);
        Assert.Equal("Inventory.LotNotFound", result.Value.Results[0].FailureReason?.Code);
    }

    // ── Empty adjustments ─────────────────────────────────────────────────────

    [Fact(DisplayName = "Empty adjustments list returns an empty success outcome")]
    public async Task Empty_Adjustments_Returns_Empty_Success_Outcome()
    {
        var (stocks, _) = StocksWithLot(100m);

        var result = await Command(stocks, []).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsSuccess);
        Assert.Empty(result.Value.Results);
        // No transaction needed for empty batch.
        Assert.Equal(0, stocks.TransactionScopes);
    }

    // ── No stock root ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "No stock root: returns NoStock failure inside the outcome")]
    public async Task NoStockRoot_Returns_NoStock_Failure_In_Outcome()
    {
        var stocks = new FakeProductStockRepository(); // empty — no stock for product

        var adjustments = new List<LotAdjustItem>
        {
            new(EntryId: null, Amount: 50m, UnitId: _unitId),
        };

        var result = await Command(stocks, adjustments).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsSuccess);
        Assert.Equal("Inventory.NoStock", result.Value.FailureReason?.Code);
    }

    // ── Auth guard ────────────────────────────────────────────────────────────

    [Fact(DisplayName = "Returns Unauthorized when no household in context")]
    public async Task Returns_Unauthorized_When_No_Household()
    {
        var (stocks, _) = StocksWithLot(100m);

        var result = await new SaveLotAdjustmentsCommand(
            _productId, _locationId, [], _userId, stocks,
            new FakeConversionProvider(new IdentityQuantityConverter()),
            Clock,
            new FakeTenantContext(null)).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    // ── Transaction boundary ──────────────────────────────────────────────────

    [Fact(DisplayName = "All adjustments run inside a single transaction per product")]
    public async Task All_Adjustments_Run_In_Single_Transaction()
    {
        var (stocks, entryId) = StocksWithLot(200m);

        var adjustments = new List<LotAdjustItem>
        {
            new(entryId, 10m, _unitId, StockReason.Correction),
            new(EntryId: null, Amount: 20m, UnitId: _unitId),
        };

        var result = await Command(stocks, adjustments).ExecuteAsync();

        Assert.True(result.IsSuccess);
        // One transaction wraps all adjustments for the product.
        Assert.Equal(1, stocks.TransactionScopes);
        // SaveChanges called once (at the end of the transaction, not per-item).
        Assert.Equal(1, stocks.SaveChangesCalls);
    }

    // ── Mixed batch: reduce + found stock ─────────────────────────────────────

    [Fact(DisplayName = "Mixed batch: reduce one lot and add found stock in same command")]
    public async Task Mixed_Batch_Reduce_And_FoundStock()
    {
        var (stocks, entryId) = StocksWithLot(100m);

        var adjustments = new List<LotAdjustItem>
        {
            new(entryId, 25m, _unitId, StockReason.Correction),
            new(EntryId: null, Amount: 60m, UnitId: _unitId, ExpiryDate: new DateOnly(2027, 3, 1)),
        };

        var result = await Command(stocks, adjustments).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsSuccess);
        Assert.Equal(2, result.Value.Results.Count);
        Assert.All(result.Value.Results, r => Assert.True(r.IsSuccess));

        var entries = stocks.Items.Single().Entries;
        // Original lot reduced by 25.
        Assert.Equal(75m, entries.First().Quantity);
        // New found-stock lot.
        Assert.Equal(60m, entries.Last().Quantity);
        Assert.Equal(new DateOnly(2027, 3, 1), entries.Last().ExpiryDate);

        var journal = stocks.Items.Single().Journal;
        Assert.Equal(3, journal.Count); // original intake + reduce + correction
        Assert.Equal(-25m, journal[^2].Delta); // penultimate = reduce
        Assert.Equal(+60m, journal[^1].Delta); // last = found stock correction
    }
}
