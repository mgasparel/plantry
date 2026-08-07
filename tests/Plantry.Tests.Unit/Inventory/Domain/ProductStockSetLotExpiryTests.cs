using Plantry.Pantry.Domain;
using Plantry.SharedKernel;

namespace Plantry.Tests.Unit.Inventory.Domain;

/// <summary>
/// L1 unit tests for <see cref="ProductStock.SetLotExpiry"/> (plantry-fyvr) — the Take Stock lot
/// panel's manual expiry correction. Mirrors <c>ProductStockMarkOpenedTests</c>'s fixture shape.
/// </summary>
public sealed class ProductStockSetLotExpiryTests
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

    [Fact(DisplayName = "SetLotExpiry overwrites the lot's expiry verbatim")]
    public void SetLotExpiry_Overwrites_Expiry()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(1m, Unit, Location, User, clock, expiryDate: Day(5));

        var result = stock.SetLotExpiry(lot.Id, Day(30), clock);

        Assert.True(result.IsSuccess);
        Assert.Equal(Day(30), lot.ExpiryDate);
        Assert.Equal(Day(30), result.Value.ExpiryDate);
        Assert.Equal(lot.Id, result.Value.EntryId);
    }

    [Fact(DisplayName = "SetLotExpiry can clear an existing expiry to null")]
    public void SetLotExpiry_Can_Clear_To_Null()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(1m, Unit, Location, User, clock, expiryDate: Day(5));

        var result = stock.SetLotExpiry(lot.Id, null, clock);

        Assert.True(result.IsSuccess);
        Assert.Null(lot.ExpiryDate);
        Assert.Null(result.Value.ExpiryDate);
    }

    [Fact(DisplayName = "SetLotExpiry can set an expiry on a lot that previously had none")]
    public void SetLotExpiry_Can_Set_From_Null()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(1m, Unit, Location, User, clock, expiryDate: null);

        var result = stock.SetLotExpiry(lot.Id, Day(10), clock);

        Assert.True(result.IsSuccess);
        Assert.Equal(Day(10), lot.ExpiryDate);
    }

    [Fact(DisplayName = "SetLotExpiry writes no journal row and changes no quantity")]
    public void SetLotExpiry_Not_Consumption()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(5m, Unit, Location, User, clock, expiryDate: Day(5));
        var journalCountBefore = stock.Journal.Count;

        stock.SetLotExpiry(lot.Id, Day(30), clock);

        Assert.Equal(5m, lot.Quantity);
        Assert.Equal(journalCountBefore, stock.Journal.Count);
    }

    [Fact(DisplayName = "SetLotExpiry on an unknown lot fails loudly")]
    public void SetLotExpiry_UnknownLot_Fails()
    {
        var stock = NewStock(out var clock);

        var result = stock.SetLotExpiry(StockEntryId.New(), Day(30), clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.LotNotFound", result.Error.Code);
    }

    [Fact(DisplayName = "SetLotExpiry on a depleted lot fails loudly")]
    public void SetLotExpiry_DepletedLot_Fails()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(1m, Unit, Location, User, clock, expiryDate: Day(5));
        stock.Consume(1m, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock);

        var result = stock.SetLotExpiry(lot.Id, Day(30), clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.LotNotActive", result.Error.Code);
    }
}
