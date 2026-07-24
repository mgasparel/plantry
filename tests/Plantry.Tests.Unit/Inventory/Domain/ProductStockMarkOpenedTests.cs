using Plantry.Inventory.Domain;
using Plantry.SharedKernel;

namespace Plantry.Tests.Unit.Inventory.Domain;

/// <summary>
/// L1 unit tests for the "opened" transitions (plantry-1le6): <see cref="ProductStock.MarkOpened"/>,
/// <see cref="ProductStock.UnmarkOpened"/>, and the auto-open step folded into
/// <see cref="ProductStock.Consume"/> (rule 5). Mirrors <c>ProductStockConsumeTests</c>'s fixture shape.
/// </summary>
public sealed class ProductStockMarkOpenedTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly Guid Unit = Guid.NewGuid();
    private static readonly Guid Location = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    private static ProductStock NewStock(out MutableClock clock)
    {
        clock = new MutableClock();
        return ProductStock.Start(Household, Product, clock);
    }

    private static DateOnly Day(int n) => new DateOnly(2026, 1, 1).AddDays(n);

    // ── MarkOpened ─────────────────────────────────────────────────────────────

    [Fact(DisplayName = "MarkOpened with a default configured clamps to today + N when that is earlier than the printed date")]
    public void MarkOpened_Clamps_When_Candidate_Earlier_Than_Printed_Date()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(1m, Unit, Location, User, clock, expiryDate: Day(90)); // far-off printed date

        var result = stock.MarkOpened(lot.Id, dueDaysAfterOpening: 5, clock); // today (Day 0) + 5 = Day 5

        Assert.True(result.IsSuccess);
        Assert.True(lot.IsOpen);
        Assert.Equal(Day(5), lot.ExpiryDate);
        Assert.Equal(Day(5), result.Value.ExpiryDate);
        Assert.True(result.Value.DefaultApplied);
    }

    [Fact(DisplayName = "MarkOpened with a default configured keeps the printed date when it is already sooner than the candidate")]
    public void MarkOpened_Keeps_Existing_Expiry_When_Sooner_Than_Candidate()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(1m, Unit, Location, User, clock, expiryDate: Day(2)); // prints sooner than the opening default

        var result = stock.MarkOpened(lot.Id, dueDaysAfterOpening: 30, clock); // today + 30 = Day 30, later than Day 2

        Assert.True(result.IsSuccess);
        Assert.True(lot.IsOpen);
        Assert.Equal(Day(2), lot.ExpiryDate); // opening never extends
        Assert.True(result.Value.DefaultApplied);
    }

    [Fact(DisplayName = "MarkOpened with a null existing expiry takes the candidate outright")]
    public void MarkOpened_NullExistingExpiry_TakesCandidate()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(1m, Unit, Location, User, clock, expiryDate: null);

        var result = stock.MarkOpened(lot.Id, dueDaysAfterOpening: 10, clock);

        Assert.True(result.IsSuccess);
        Assert.Equal(Day(10), lot.ExpiryDate);
    }

    [Fact(DisplayName = "MarkOpened with no default anywhere flips the flag but leaves the expiry untouched")]
    public void MarkOpened_NoDefault_FlipsFlag_LeavesExpiry()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(1m, Unit, Location, User, clock, expiryDate: Day(14));

        var result = stock.MarkOpened(lot.Id, dueDaysAfterOpening: null, clock);

        Assert.True(result.IsSuccess);
        Assert.True(lot.IsOpen);
        Assert.Equal(Day(14), lot.ExpiryDate); // untouched
        Assert.False(result.Value.DefaultApplied);
    }

    [Fact(DisplayName = "MarkOpened writes no journal row and changes no quantity (rule 6 — not consumption)")]
    public void MarkOpened_Not_Consumption()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(5m, Unit, Location, User, clock, expiryDate: Day(14));
        var journalCountBefore = stock.Journal.Count;

        stock.MarkOpened(lot.Id, dueDaysAfterOpening: 5, clock);

        Assert.Equal(5m, lot.Quantity);
        Assert.Equal(journalCountBefore, stock.Journal.Count);
    }

    [Fact(DisplayName = "MarkOpened on an unknown lot fails loudly")]
    public void MarkOpened_UnknownLot_Fails()
    {
        var stock = NewStock(out var clock);

        var result = stock.MarkOpened(StockEntryId.New(), dueDaysAfterOpening: 5, clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.LotNotFound", result.Error.Code);
    }

    [Fact(DisplayName = "MarkOpened on a depleted lot fails loudly")]
    public void MarkOpened_DepletedLot_Fails()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(1m, Unit, Location, User, clock, expiryDate: Day(1));
        stock.Consume(1m, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock);

        var result = stock.MarkOpened(lot.Id, dueDaysAfterOpening: 5, clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.LotNotActive", result.Error.Code);
    }

    [Fact(DisplayName = "MarkOpened on an already-open lot fails loudly")]
    public void MarkOpened_AlreadyOpen_Fails()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(1m, Unit, Location, User, clock, expiryDate: Day(14));
        stock.MarkOpened(lot.Id, dueDaysAfterOpening: 5, clock);

        var result = stock.MarkOpened(lot.Id, dueDaysAfterOpening: 5, clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.LotAlreadyOpen", result.Error.Code);
    }

    // ── UnmarkOpened ───────────────────────────────────────────────────────────

    [Fact(DisplayName = "UnmarkOpened clears the flag but does NOT restore the pre-opening expiry")]
    public void UnmarkOpened_Clears_Flag_Without_Restoring_Expiry()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(1m, Unit, Location, User, clock, expiryDate: Day(90));
        stock.MarkOpened(lot.Id, dueDaysAfterOpening: 5, clock); // recomputes to Day 5

        var result = stock.UnmarkOpened(lot.Id, clock);

        Assert.True(result.IsSuccess);
        Assert.False(lot.IsOpen);
        Assert.Equal(Day(5), lot.ExpiryDate); // NOT restored to Day 90
        Assert.Equal(Day(5), result.Value.ExpiryDate);
    }

    [Fact(DisplayName = "UnmarkOpened on an unknown lot fails loudly")]
    public void UnmarkOpened_UnknownLot_Fails()
    {
        var stock = NewStock(out var clock);

        var result = stock.UnmarkOpened(StockEntryId.New(), clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.LotNotFound", result.Error.Code);
    }

    [Fact(DisplayName = "UnmarkOpened on a sealed lot fails loudly")]
    public void UnmarkOpened_SealedLot_Fails()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(1m, Unit, Location, User, clock, expiryDate: Day(14));

        var result = stock.UnmarkOpened(lot.Id, clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.LotNotOpen", result.Error.Code);
    }

    // ── Auto-open on partial consume (rule 5) ───────────────────────────────────

    [Fact(DisplayName = "A partial consume on a sealed lot auto-opens it and recomputes the clamped expiry")]
    public void Consume_Partial_On_Sealed_Lot_AutoOpens_AndRecomputes()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(10m, Unit, Location, User, clock, expiryDate: Day(90));

        var result = stock.Consume(
            4m, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock,
            dueDaysAfterOpening: 5);

        Assert.True(result.IsSuccess);
        Assert.True(lot.IsOpen);
        Assert.Equal(Day(5), lot.ExpiryDate);
        Assert.Equal(6m, lot.Quantity); // 10 - 4, still active
        var opened = Assert.Single(result.Value.AutoOpened);
        Assert.Equal(lot.Id, opened.EntryId);
        Assert.Equal(Day(5), opened.ExpiryDate);
        Assert.True(opened.DefaultApplied);
    }

    [Fact(DisplayName = "Consuming a sealed lot to full depletion does NOT auto-open it (nothing left to expire)")]
    public void Consume_FullDepletion_Does_Not_AutoOpen()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(4m, Unit, Location, User, clock, expiryDate: Day(90));

        var result = stock.Consume(
            4m, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock,
            dueDaysAfterOpening: 5);

        Assert.True(result.IsSuccess);
        Assert.False(lot.IsOpen);
        Assert.Equal(Day(90), lot.ExpiryDate); // untouched
        Assert.Empty(result.Value.AutoOpened);
    }

    [Fact(DisplayName = "A partial consume on an already-open lot does not re-fire the recompute")]
    public void Consume_Partial_On_AlreadyOpen_Lot_Does_Not_Refire()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(10m, Unit, Location, User, clock, expiryDate: Day(90));
        stock.MarkOpened(lot.Id, dueDaysAfterOpening: 5, clock); // expiry now Day 5

        var result = stock.Consume(
            4m, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock,
            dueDaysAfterOpening: 20); // a different default — must NOT be applied since it's already open

        Assert.True(result.IsSuccess);
        Assert.True(lot.IsOpen);
        Assert.Equal(Day(5), lot.ExpiryDate); // unchanged by this consume
        Assert.Empty(result.Value.AutoOpened);
    }

    [Fact(DisplayName = "A partial consume on a sealed lot with no default configured still flips IsOpen, expiry untouched (rule 4)")]
    public void Consume_Partial_NoDefault_FlipsFlag_LeavesExpiry()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(10m, Unit, Location, User, clock, expiryDate: Day(14));

        var result = stock.Consume(
            4m, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock,
            dueDaysAfterOpening: null);

        Assert.True(result.IsSuccess);
        Assert.True(lot.IsOpen);
        Assert.Equal(Day(14), lot.ExpiryDate);
        var opened = Assert.Single(result.Value.AutoOpened);
        Assert.False(opened.DefaultApplied);
    }

    [Fact(DisplayName = "A multi-lot partial consume can auto-open more than one sealed lot")]
    public void Consume_MultiLot_AutoOpens_Every_Sealed_Partial_Lot()
    {
        var stock = NewStock(out var clock);
        var lotA = stock.AddStock(3m, Unit, Location, User, clock.Advance(TimeSpan.FromMinutes(1)), expiryDate: Day(1));
        var lotB = stock.AddStock(5m, Unit, Location, User, clock.Advance(TimeSpan.FromMinutes(1)), expiryDate: Day(2));

        // Consumes 6: fully depletes lotA (FEFO-first, no auto-open), partially deducts lotB (auto-opens).
        var result = stock.Consume(
            6m, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock,
            dueDaysAfterOpening: 3);

        Assert.True(result.IsSuccess);
        Assert.True(lotA.IsDepleted);
        Assert.False(lotA.IsOpen); // fully depleted — never opened
        Assert.False(lotB.IsDepleted);
        Assert.True(lotB.IsOpen);
        var opened = Assert.Single(result.Value.AutoOpened);
        Assert.Equal(lotB.Id, opened.EntryId);
    }
}
