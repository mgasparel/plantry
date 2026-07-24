using Plantry.Inventory.Domain;
using Plantry.SharedKernel;

namespace Plantry.Tests.Unit.Inventory.Domain;

/// <summary>
/// L1 unit tests for the transfer/freeze/thaw primitive (plantry-6owm): <see cref="ProductStock.Transfer"/>.
/// Mirrors <c>ProductStockMarkOpenedTests</c>'s fixture shape. The transition kind (Freeze/Thaw/Move) is
/// derived from the caller-supplied source/destination frozen-ness flags — the Catalog fact resolution
/// itself is covered at the application layer in <c>TransferStockCommandTests</c>.
/// </summary>
public sealed class ProductStockTransferTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly Guid Unit = Guid.NewGuid();
    private static readonly Guid Fridge = Guid.NewGuid();
    private static readonly Guid Freezer = Guid.NewGuid();
    private static readonly Guid Pantry = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    private static ProductStock NewStock(out MutableClock clock)
    {
        clock = new MutableClock();
        return ProductStock.Start(Household, Product, clock);
    }

    private static DateOnly Day(int n) => new DateOnly(2026, 1, 1).AddDays(n);

    // ── Full-lot freeze / thaw ───────────────────────────────────────────────────

    [Fact(DisplayName = "Full-lot freeze moves the lot, sets FrozenAt, and replaces the expiry outright (may extend)")]
    public void FullLot_Freeze_MovesSetsFrozenAt_ReplacesExpiry()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(2m, Unit, Fridge, User, clock, expiryDate: Day(5)); // printed date soon

        var result = stock.Transfer(
            lot.Id, Freezer, sourceIsFrozen: false, destinationIsFrozen: true,
            quantity: 2m, clock, dueDaysAfterFreezing: 90, dueDaysAfterThawing: 2);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferKind.Freeze, result.Value.Kind);
        Assert.Null(result.Value.SplitEntryId);
        Assert.Equal(Freezer, lot.LocationId);
        Assert.NotNull(lot.FrozenAt);
        Assert.Equal(Day(90), lot.ExpiryDate); // extended past the Day(5) printed date — intended
        Assert.True(result.Value.DefaultApplied);
        Assert.Equal(Day(90), result.Value.ExpiryDate);
    }

    [Fact(DisplayName = "Full-lot thaw moves the lot, sets ThawedAt, and replaces the expiry outright (tightens)")]
    public void FullLot_Thaw_MovesSetsThawedAt_ReplacesExpiry()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(2m, Unit, Freezer, User, clock, expiryDate: Day(90));

        var result = stock.Transfer(
            lot.Id, Fridge, sourceIsFrozen: true, destinationIsFrozen: false,
            quantity: 2m, clock, dueDaysAfterFreezing: 90, dueDaysAfterThawing: 2);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferKind.Thaw, result.Value.Kind);
        Assert.Equal(Fridge, lot.LocationId);
        Assert.NotNull(lot.ThawedAt);
        Assert.Equal(Day(2), lot.ExpiryDate); // tightened from the Day(90) printed date
        Assert.True(result.Value.DefaultApplied);
    }

    // ── Partial transfer (split) ─────────────────────────────────────────────────

    [Fact(DisplayName = "Partial freeze splits: source keeps location/expiry with the reduced qty; the moved portion becomes a new lot with recomputed expiry")]
    public void Partial_Freeze_Splits_SourceUntouched_NewLotRecomputed()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(5m, Unit, Fridge, User, clock, expiryDate: Day(5), purchasedAt: Day(-1));

        var result = stock.Transfer(
            lot.Id, Freezer, sourceIsFrozen: false, destinationIsFrozen: true,
            quantity: 2m, clock, dueDaysAfterFreezing: 90, dueDaysAfterThawing: 2);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value.SplitEntryId);

        // Source lot: untouched location/expiry, reduced quantity.
        Assert.Equal(Fridge, lot.LocationId);
        Assert.Equal(Day(5), lot.ExpiryDate);
        Assert.Null(lot.FrozenAt);
        Assert.Equal(3m, lot.Quantity);
        Assert.True(lot.IsActive);

        // New lot: at the destination, recomputed expiry, inherited PurchasedAt/unit.
        var newLot = stock.Entries.Single(e => e.Id == result.Value.SplitEntryId!.Value);
        Assert.Equal(Freezer, newLot.LocationId);
        Assert.Equal(Day(90), newLot.ExpiryDate);
        Assert.NotNull(newLot.FrozenAt);
        Assert.Equal(2m, newLot.Quantity);
        Assert.Equal(Unit, newLot.UnitId);
        Assert.Equal(Day(-1), newLot.PurchasedAt);

        // Quantities sum to the original.
        Assert.Equal(5m, lot.Quantity + newLot.Quantity);
    }

    // ── Plain move ────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "Plain move (ambient→ambient) leaves expiry and timestamps untouched")]
    public void PlainMove_AmbientToAmbient_LeavesExpiryAndTimestampsUntouched()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(2m, Unit, Fridge, User, clock, expiryDate: Day(10));

        var result = stock.Transfer(
            lot.Id, Pantry, sourceIsFrozen: false, destinationIsFrozen: false,
            quantity: 2m, clock, dueDaysAfterFreezing: 90, dueDaysAfterThawing: 2);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferKind.Move, result.Value.Kind);
        Assert.Equal(Pantry, lot.LocationId);
        Assert.Equal(Day(10), lot.ExpiryDate);
        Assert.Null(lot.FrozenAt);
        Assert.Null(lot.ThawedAt);
        Assert.False(result.Value.DefaultApplied);
    }

    [Fact(DisplayName = "Plain move (frozen→frozen) leaves expiry and timestamps untouched")]
    public void PlainMove_FrozenToFrozen_LeavesExpiryAndTimestampsUntouched()
    {
        var stock = NewStock(out var clock);
        var otherFreezer = Guid.NewGuid();
        var lot = stock.AddStock(2m, Unit, Freezer, User, clock, expiryDate: Day(60));

        var result = stock.Transfer(
            lot.Id, otherFreezer, sourceIsFrozen: true, destinationIsFrozen: true,
            quantity: 2m, clock, dueDaysAfterFreezing: 90, dueDaysAfterThawing: 2);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferKind.Move, result.Value.Kind);
        Assert.Equal(Day(60), lot.ExpiryDate);
        Assert.Null(lot.FrozenAt);
    }

    // ── No default configured (rule 6) ───────────────────────────────────────────

    [Fact(DisplayName = "Freeze with no after-freezing default still moves and sets FrozenAt, expiry untouched")]
    public void Freeze_NoDefault_MovesAndSetsTimestamp_LeavesExpiry()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(1m, Unit, Fridge, User, clock, expiryDate: Day(14));

        var result = stock.Transfer(
            lot.Id, Freezer, sourceIsFrozen: false, destinationIsFrozen: true,
            quantity: 1m, clock, dueDaysAfterFreezing: null, dueDaysAfterThawing: null);

        Assert.True(result.IsSuccess);
        Assert.Equal(Freezer, lot.LocationId);
        Assert.NotNull(lot.FrozenAt);
        Assert.Equal(Day(14), lot.ExpiryDate); // untouched
        Assert.False(result.Value.DefaultApplied);
    }

    [Fact(DisplayName = "Thaw with no after-thawing default still moves and sets ThawedAt, expiry untouched")]
    public void Thaw_NoDefault_MovesAndSetsTimestamp_LeavesExpiry()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(1m, Unit, Freezer, User, clock, expiryDate: Day(14));

        var result = stock.Transfer(
            lot.Id, Fridge, sourceIsFrozen: true, destinationIsFrozen: false,
            quantity: 1m, clock, dueDaysAfterFreezing: null, dueDaysAfterThawing: null);

        Assert.True(result.IsSuccess);
        Assert.NotNull(lot.ThawedAt);
        Assert.Equal(Day(14), lot.ExpiryDate);
        Assert.False(result.Value.DefaultApplied);
    }

    // ── Repeat transitions / refreeze (rule 4) ───────────────────────────────────

    [Fact(DisplayName = "Refreeze after a thaw overwrites FrozenAt; ThawedAt is retained independently (own-field, no history)")]
    public void Refreeze_OverwritesFrozenAt_RetainsThawedAt()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(1m, Unit, Freezer, User, clock, expiryDate: Day(90));

        stock.Transfer(lot.Id, Fridge, true, false, 1m, clock, 90, 2); // thaw
        var thawedAt = lot.ThawedAt;
        Assert.NotNull(thawedAt);

        clock.Advance(TimeSpan.FromMinutes(5));
        var result = stock.Transfer(lot.Id, Freezer, false, true, 1m, clock, 90, 2); // refreeze

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferKind.Freeze, result.Value.Kind);
        Assert.NotEqual(thawedAt, lot.FrozenAt); // FrozenAt updated to the refreeze instant
        Assert.Equal(thawedAt, lot.ThawedAt); // ThawedAt untouched by the freeze
    }

    // ── Not consumption (rule 7) ──────────────────────────────────────────────────

    [Fact(DisplayName = "Transfer writes no journal row and does not change product-level on-hand accounting")]
    public void Transfer_Not_Consumption()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(5m, Unit, Fridge, User, clock, expiryDate: Day(14));
        var journalCountBefore = stock.Journal.Count;

        stock.Transfer(lot.Id, Freezer, false, true, 5m, clock, 90, 2);

        Assert.Equal(journalCountBefore, stock.Journal.Count);
        Assert.Equal(5m, lot.Quantity); // full-lot move — no quantity lost
    }

    // ── Guards ────────────────────────────────────────────────────────────────────

    [Theory(DisplayName = "Zero/negative/over-quantity transfers are rejected")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(6)]
    public void InvalidQuantity_Rejected(decimal quantity)
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(5m, Unit, Fridge, User, clock, expiryDate: Day(14));

        var result = stock.Transfer(lot.Id, Freezer, false, true, quantity, clock, 90, 2);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.InvalidTransferQuantity", result.Error.Code);
    }

    [Fact(DisplayName = "Destination equal to the current location is rejected")]
    public void SameLocation_Rejected()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(5m, Unit, Fridge, User, clock, expiryDate: Day(14));

        var result = stock.Transfer(lot.Id, Fridge, false, false, 5m, clock, 90, 2);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.SameLocation", result.Error.Code);
    }

    [Fact(DisplayName = "Transfer on an unknown lot fails loudly")]
    public void UnknownLot_Fails()
    {
        var stock = NewStock(out var clock);

        var result = stock.Transfer(StockEntryId.New(), Freezer, false, true, 1m, clock, 90, 2);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.LotNotFound", result.Error.Code);
    }

    [Fact(DisplayName = "Transfer on a depleted lot fails loudly")]
    public void DepletedLot_Fails()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(1m, Unit, Fridge, User, clock, expiryDate: Day(1));
        stock.Consume(1m, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock);

        var result = stock.Transfer(lot.Id, Freezer, false, true, 1m, clock, 90, 2);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.LotNotActive", result.Error.Code);
    }
}
