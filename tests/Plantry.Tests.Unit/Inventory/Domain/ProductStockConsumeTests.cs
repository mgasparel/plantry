using Plantry.Inventory.Domain;
using Plantry.SharedKernel;

namespace Plantry.Tests.Unit.Inventory.Domain;

/// <summary>
/// L1 unit tests for the single consumption primitive (ADR-011): FEFO ordering with nulls-last and
/// the entry_id tiebreaker, multi-lot deduction, depletion, shortfall reporting, targeted-lot
/// consume, the reason taxonomy, and fail-loud conversion with no partial mutation.
/// </summary>
public sealed class ProductStockConsumeTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly Guid Unit = Guid.NewGuid();      // the single unit most tests use
    private static readonly Guid Location = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    private static ProductStock NewStock(out MutableClock clock)
    {
        clock = new MutableClock();
        return ProductStock.Start(Household, Product, clock);
    }

    private static DateOnly Day(int n) => new DateOnly(2026, 1, 1).AddDays(n);

    [Fact(DisplayName = "FEFO consumes the soonest-expiring lot first, treating null expiry as last")]
    public void Consume_Fefo_Orders_SoonestExpiry_First_NullsLast()
    {
        var stock = NewStock(out var clock);
        var farLot = stock.AddStock(5m, Unit, Location, User, clock.Advance(TimeSpan.FromMinutes(1)), expiryDate: Day(5));
        var noExpiryLot = stock.AddStock(5m, Unit, Location, User, clock.Advance(TimeSpan.FromMinutes(1)), expiryDate: null);
        var soonLot = stock.AddStock(5m, Unit, Location, User, clock.Advance(TimeSpan.FromMinutes(1)), expiryDate: Day(1));

        var result = stock.Consume(3m, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock);

        Assert.True(result.IsSuccess);
        var deduction = Assert.Single(result.Value.Deductions);
        Assert.Equal(soonLot.Id, deduction.EntryId);
        Assert.Equal(2m, soonLot.Quantity);
        Assert.Equal(5m, farLot.Quantity);
        Assert.Equal(5m, noExpiryLot.Quantity);
    }

    [Fact(DisplayName = "Lots sharing expiry and creation instant break the tie deterministically on entry_id")]
    public void Consume_Fefo_BreaksTies_On_EntryId()
    {
        var stock = NewStock(out var clock);
        // Same expiry, same clock instant — only entry_id separates them. UUIDv7 is only monotonic
        // across milliseconds, so within one instant the total order follows the id value itself.
        var a = stock.AddStock(2m, Unit, Location, User, clock, expiryDate: Day(3));
        var b = stock.AddStock(2m, Unit, Location, User, clock, expiryDate: Day(3));

        var (lower, higher) = a.Id.Value.CompareTo(b.Id.Value) < 0 ? (a, b) : (b, a);

        var result = stock.Consume(2m, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock);

        Assert.True(result.IsSuccess);
        Assert.Equal(lower.Id, Assert.Single(result.Value.Deductions).EntryId);
        Assert.True(lower.IsDepleted);
        Assert.Equal(2m, higher.Quantity);
    }

    [Fact(DisplayName = "A consume larger than one lot deducts across lots in FEFO order")]
    public void Consume_Deducts_Across_Multiple_Lots()
    {
        var stock = NewStock(out var clock);
        var lotA = stock.AddStock(3m, Unit, Location, User, clock, expiryDate: Day(1));
        var lotB = stock.AddStock(5m, Unit, Location, User, clock, expiryDate: Day(2));

        var result = stock.Consume(6m, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Deductions.Count);
        Assert.False(result.Value.HasShortfall);
        Assert.True(lotA.IsDepleted);
        Assert.Equal(0m, lotA.Quantity);
        Assert.Equal(2m, lotB.Quantity);
    }

    [Fact(DisplayName = "Consuming a lot exactly to zero marks it depleted but retains the row")]
    public void Consume_Exact_Sets_DepletedAt_And_Retains_Lot()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(4m, Unit, Location, User, clock, expiryDate: Day(1));

        var result = stock.Consume(4m, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock);

        Assert.True(result.IsSuccess);
        Assert.True(lot.IsDepleted);
        Assert.Equal(0m, lot.Quantity);
        Assert.Contains(stock.Entries, e => e.Id == lot.Id); // row kept for journal integrity
    }

    [Fact(DisplayName = "Consuming more than is on hand deducts everything and reports the shortfall, never going negative")]
    public void Consume_Beyond_Stock_Reports_Shortfall()
    {
        var stock = NewStock(out var clock);
        var lotA = stock.AddStock(1m, Unit, Location, User, clock, expiryDate: Day(1));
        var lotB = stock.AddStock(3m, Unit, Location, User, clock, expiryDate: Day(2));

        var result = stock.Consume(10m, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.HasShortfall);
        Assert.Equal(6m, result.Value.ShortfallAmount);
        Assert.Equal(Unit, result.Value.RequestUnitId);
        Assert.True(lotA.IsDepleted);
        Assert.True(lotB.IsDepleted);
        Assert.Equal(0m, lotA.Quantity);
        Assert.Equal(0m, lotB.Quantity);
    }

    [Fact(DisplayName = "A targeted consume deducts only the named lot, bypassing FEFO")]
    public void Consume_Targeted_Lot_Bypasses_Fefo()
    {
        var stock = NewStock(out var clock);
        var soonLot = stock.AddStock(5m, Unit, Location, User, clock, expiryDate: Day(1));
        var laterLot = stock.AddStock(5m, Unit, Location, User, clock, expiryDate: Day(9));

        // "This carton is spoiled" — discard the later lot specifically (§1b/§1d).
        var result = stock.Consume(
            5m, Unit, StockReason.Discarded, new IdentityQuantityConverter(), User, clock, targetEntry: laterLot.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(laterLot.Id, Assert.Single(result.Value.Deductions).EntryId);
        Assert.True(laterLot.IsDepleted);
        Assert.Equal(5m, soonLot.Quantity);
    }

    [Fact(DisplayName = "Targeting a lot that has no active stock fails loudly")]
    public void Consume_Targeted_Missing_Lot_Fails()
    {
        var stock = NewStock(out var clock);

        var result = stock.Consume(
            1m, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock, targetEntry: StockEntryId.New());

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.LotNotFound", result.Error.Code);
    }

    [Theory(DisplayName = "The removal reason is recorded verbatim on each journal row")]
    [InlineData(StockReason.Consumed)]
    [InlineData(StockReason.Discarded)]
    [InlineData(StockReason.Correction)]
    public void Consume_Records_Reason_On_Journal(StockReason reason)
    {
        var stock = NewStock(out var clock);
        stock.AddStock(5m, Unit, Location, User, clock, expiryDate: Day(1));

        var result = stock.Consume(2m, Unit, reason, new IdentityQuantityConverter(), User, clock);

        Assert.True(result.IsSuccess);
        var removal = Assert.Single(stock.Journal, j => j.Reason != StockReason.Purchase);
        Assert.Equal(reason, removal.Reason);
        Assert.Equal(-2m, removal.Delta);
    }

    [Fact(DisplayName = "Consume cannot record a Purchase reason")]
    public void Consume_Rejects_Purchase_Reason()
    {
        var stock = NewStock(out var clock);
        stock.AddStock(5m, Unit, Location, User, clock, expiryDate: Day(1));

        var result = stock.Consume(1m, Unit, StockReason.Purchase, new IdentityQuantityConverter(), User, clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.InvalidConsumeReason", result.Error.Code);
    }

    [Theory(DisplayName = "A non-positive consume amount is rejected")]
    [InlineData(0)]
    [InlineData(-3)]
    public void Consume_Rejects_NonPositive_Amount(int amount)
    {
        var stock = NewStock(out var clock);
        stock.AddStock(5m, Unit, Location, User, clock, expiryDate: Day(1));

        var result = stock.Consume(amount, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.InvalidConsumeAmount", result.Error.Code);
    }

    [Fact(DisplayName = "An unresolvable unit fails loudly and leaves every lot untouched")]
    public void Consume_Unresolvable_Conversion_Fails_Without_Mutating()
    {
        var stock = NewStock(out var clock);
        var requestUnit = Guid.NewGuid(); // differs from the lot unit, and the converter can't bridge it
        var lot = stock.AddStock(5m, Unit, Location, User, clock, expiryDate: Day(1));

        var result = stock.Consume(2m, requestUnit, StockReason.Consumed, new FailingQuantityConverter(), User, clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Test.Unresolvable", result.Error.Code);
        Assert.Equal(5m, lot.Quantity);                              // not mutated
        Assert.DoesNotContain(stock.Journal, j => j.Reason != StockReason.Purchase); // no removal row written
    }

    [Fact(DisplayName = "Consume converts the requested unit into each lot's unit")]
    public void Consume_Converts_Request_Unit_To_Lot_Unit()
    {
        var stock = NewStock(out var clock);
        var grams = Guid.NewGuid();
        var kilograms = Guid.NewGuid();
        var converter = new FactorQuantityConverter(new()
        {
            [(kilograms, grams)] = 1000m,    // 1 kg = 1000 g
            [(grams, kilograms)] = 0.001m,
        });
        var lot = stock.AddStock(600m, grams, Location, User, clock, expiryDate: Day(1)); // 600 g on hand

        var result = stock.Consume(0.5m, kilograms, StockReason.Consumed, converter, User, clock); // ask for 0.5 kg

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.HasShortfall);
        Assert.Equal(100m, lot.Quantity);                 // 600 g − 500 g
        var deduction = Assert.Single(result.Value.Deductions);
        Assert.Equal(500m, deduction.Amount);             // recorded in the lot's unit
        Assert.Equal(grams, deduction.UnitId);
    }

    // ── Idempotency (plantry-292a) ────────────────────────────────────────────

    [Fact(DisplayName = "Re-driving a consume with the same sourceLineRef is a no-op — no journal row, no stock change")]
    public void Consume_WithSourceLineRef_IsIdempotent_OnRedrive()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(100m, Unit, Location, User, clock, expiryDate: Day(1));
        var token = Guid.NewGuid();

        // First consume — should apply and write a journal row.
        var first = stock.Consume(30m, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock,
            sourceLineRef: token);

        Assert.True(first.IsSuccess);
        Assert.Equal(1, first.Value.Deductions.Count);
        Assert.Equal(70m, lot.Quantity);
        // Journal: 1 Purchase + 1 Consumed (2 total, 1 removal).
        Assert.Equal(1, stock.Journal.Count(j => j.Reason != StockReason.Purchase));

        // Second consume with the same token — must be a no-op.
        var second = stock.Consume(30m, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock,
            sourceLineRef: token);

        Assert.True(second.IsSuccess);
        Assert.Empty(second.Value.Deductions);       // no lots touched
        Assert.False(second.Value.HasShortfall);     // zero shortfall (not a real shortage)
        Assert.Equal(70m, lot.Quantity);             // unchanged — idempotent
        // Still only 1 removal journal row written.
        Assert.Equal(1, stock.Journal.Count(j => j.Reason != StockReason.Purchase));
    }

    [Fact(DisplayName = "A different sourceLineRef on the same aggregate is NOT treated as a duplicate")]
    public void Consume_DifferentSourceLineRef_IsNotIdempotencyMatch()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(100m, Unit, Location, User, clock, expiryDate: Day(1));

        stock.Consume(30m, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock,
            sourceLineRef: Guid.NewGuid());

        // Second consume with a DIFFERENT token — must apply normally.
        var second = stock.Consume(30m, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock,
            sourceLineRef: Guid.NewGuid());

        Assert.True(second.IsSuccess);
        Assert.Equal(1, second.Value.Deductions.Count);
        Assert.Equal(40m, lot.Quantity); // 100 - 30 - 30 = 40
        Assert.Equal(2, stock.Journal.Count(j => j.Reason != StockReason.Purchase));
    }

    [Fact(DisplayName = "sourceLineRef is stamped on every per-lot journal row of a multi-lot consume")]
    public void Consume_SourceLineRef_StampedOnEachJournalRow()
    {
        var stock = NewStock(out var clock);
        stock.AddStock(50m, Unit, Location, User, clock, expiryDate: Day(1));
        stock.AddStock(50m, Unit, Location, User, clock, expiryDate: Day(2));
        var token = Guid.NewGuid();
        var sourceRef = Guid.NewGuid();

        stock.Consume(80m, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock,
            sourceRef: sourceRef, sourceLineRef: token);

        var removalRows = stock.Journal.Where(j => j.Reason != StockReason.Purchase).ToList();
        Assert.Equal(2, removalRows.Count); // one per lot touched
        Assert.All(removalRows, j => Assert.Equal(token, j.SourceLineRef));
        Assert.All(removalRows, j => Assert.Equal(sourceRef, j.SourceRef));
    }

    [Fact(DisplayName = "Null sourceLineRef on manual consume never short-circuits (regression)")]
    public void Consume_NullSourceLineRef_DoesNotTriggerIdempotency()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(100m, Unit, Location, User, clock, expiryDate: Day(1));

        // Two consumes both with null sourceLineRef — each should apply.
        stock.Consume(30m, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock,
            sourceLineRef: null);
        stock.Consume(30m, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock,
            sourceLineRef: null);

        Assert.Equal(40m, lot.Quantity); // 100 - 30 - 30 = 40
        Assert.Equal(2, stock.Journal.Count(j => j.Reason != StockReason.Purchase));
    }
}
