using Plantry.Inventory.Domain;
using Plantry.SharedKernel;

namespace Plantry.Tests.Unit.Inventory.Domain;

/// <summary>L1 unit tests for intake (<see cref="ProductStock.AddStock"/>) — DM-11/13.</summary>
public sealed class ProductStockAddStockTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly Guid Unit = Guid.NewGuid();
    private static readonly Guid Location = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    [Fact(DisplayName = "AddStock creates a lot and a positive Purchase journal row")]
    public void AddStock_Creates_Lot_And_Purchase_Journal_Row()
    {
        var clock = new MutableClock();
        var stock = ProductStock.Start(Household, Product, clock);
        var expiry = new DateOnly(2026, 2, 1);

        var lot = stock.AddStock(
            12m, Unit, Location, User, clock,
            expiryDate: expiry, purchasedAt: new DateOnly(2026, 1, 1), sourceType: StockSourceType.Manual);

        Assert.Equal(12m, lot.Quantity);
        Assert.Equal(Unit, lot.UnitId);
        Assert.Equal(Location, lot.LocationId);
        Assert.Equal(expiry, lot.ExpiryDate);
        Assert.False(lot.IsDepleted);
        Assert.Equal(lot, Assert.Single(stock.Entries));

        var journal = Assert.Single(stock.Journal);
        Assert.Equal(+12m, journal.Delta);
        Assert.Equal(StockReason.Purchase, journal.Reason);
        Assert.Equal(StockSourceType.Manual, journal.SourceType);
        Assert.Equal(lot.Id, journal.StockEntryId);
        Assert.Equal(User, journal.UserId);
    }

    [Fact(DisplayName = "A blank expiry is stored as null (no expiry materialized)")]
    public void AddStock_Allows_Null_Expiry()
    {
        var clock = new MutableClock();
        var stock = ProductStock.Start(Household, Product, clock);

        var lot = stock.AddStock(3m, Unit, Location, User, clock);

        Assert.Null(lot.ExpiryDate);
    }

    [Theory(DisplayName = "A non-positive intake quantity is rejected")]
    [InlineData(0)]
    [InlineData(-5)]
    public void AddStock_Rejects_NonPositive_Quantity(int quantity)
    {
        var clock = new MutableClock();
        var stock = ProductStock.Start(Household, Product, clock);

        Assert.Throws<ArgumentOutOfRangeException>(() => stock.AddStock(quantity, Unit, Location, User, clock));
    }

    [Fact(DisplayName = "Start sets the composite identity and initial tracking properties")]
    public void Start_Sets_Composite_Id_And_Properties()
    {
        var clock = new MutableClock();

        var stock = ProductStock.Start(Household, Product, clock);

        Assert.Equal(Household, stock.HouseholdId);
        Assert.Equal(Product, stock.ProductId);
        Assert.Equal(clock.UtcNow, stock.CreatedAt);
        Assert.Equal(clock.UtcNow, stock.UpdatedAt);
        Assert.Empty(stock.Entries);
        Assert.Empty(stock.Journal);
    }

    [Fact(DisplayName = "Equals and GetHashCode use the composite (HouseholdId, ProductId) key")]
    public void Equals_And_GetHashCode_Use_Composite_Key()
    {
        var clock = new MutableClock();
        var a = ProductStock.Start(Household, Product, clock);
        var b = ProductStock.Start(Household, Product, clock);   // same composite key
        var other = ProductStock.Start(HouseholdId.New(), Product, clock); // different household

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, other);
    }
}
