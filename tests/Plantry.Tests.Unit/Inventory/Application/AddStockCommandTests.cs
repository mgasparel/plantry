using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Inventory.Application;

public sealed class AddStockCommandTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _productId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();

    private FakeCatalogReadFacade CatalogWith(bool canHoldStock = true)
    {
        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(_productId, "Flour", "Baking", _unitId, "g", canHoldStock));
        return catalog;
    }

    private AddStockCommand Command(
        FakeProductStockRepository stocks, FakeCatalogReadFacade catalog, Guid? household,
        decimal quantity = 500m, DateOnly? expiry = null) =>
        new(_productId, quantity, _unitId, _locationId, _userId, null, expiry, null,
            stocks, catalog, Clock, new FakeTenantContext(household));

    [Fact]
    public async Task Adds_A_New_Lot_With_A_Positive_Purchase_Journal_Row()
    {
        var stocks = new FakeProductStockRepository();

        var result = await Command(stocks, CatalogWith(), _household, expiry: new DateOnly(2026, 7, 1)).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var stock = Assert.Single(stocks.Items);
        Assert.Equal(_household, stock.HouseholdId.Value);
        var lot = Assert.Single(stock.Entries);
        Assert.Equal(result.Value, lot.Id);
        Assert.Equal(500m, lot.Quantity);
        Assert.Equal(new DateOnly(2026, 7, 1), lot.ExpiryDate);

        var journal = Assert.Single(stock.Journal);
        Assert.Equal(StockReason.Purchase, journal.Reason);
        Assert.Equal(StockSourceType.Manual, journal.SourceType);
        Assert.Equal(500m, journal.Delta);
        Assert.Equal(1, stocks.SaveChangesCalls);
    }

    [Fact]
    public async Task Appends_A_Second_Lot_To_An_Existing_Product_Stock()
    {
        var stocks = new FakeProductStockRepository();
        var existing = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        existing.AddStock(100m, _unitId, _locationId, _userId, Clock);
        stocks.Items.Add(existing);

        var result = await Command(stocks, CatalogWith(), _household, quantity: 250m).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var stock = Assert.Single(stocks.Items); // not a second root
        Assert.Equal(2, stock.Entries.Count);
    }

    [Fact]
    public async Task Fails_When_No_Household_In_Context()
    {
        var stocks = new FakeProductStockRepository();

        var result = await Command(stocks, CatalogWith(), household: null).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
        Assert.Empty(stocks.Items);
    }

    [Fact]
    public async Task Fails_When_Product_Does_Not_Exist()
    {
        var stocks = new FakeProductStockRepository();

        var result = await Command(stocks, new FakeCatalogReadFacade(), _household).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.UnknownProduct", result.Error.Code);
        Assert.Empty(stocks.Items);
    }

    [Fact]
    public async Task Fails_When_Product_Cannot_Hold_Stock()
    {
        var stocks = new FakeProductStockRepository();

        var result = await Command(stocks, CatalogWith(canHoldStock: false), _household).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.ProductCannotHoldStock", result.Error.Code);
        Assert.Empty(stocks.Items);
    }

    [Fact]
    public async Task Fails_When_Quantity_Is_Not_Positive()
    {
        var stocks = new FakeProductStockRepository();

        var result = await Command(stocks, CatalogWith(), _household, quantity: 0m).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.InvalidQuantity", result.Error.Code);
        Assert.Empty(stocks.Items);
    }
}
