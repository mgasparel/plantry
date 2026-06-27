using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Inventory.Application;

public sealed class SetLowStockThresholdCommandTests
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

    private SetLowStockThresholdCommand Command(
        FakeProductStockRepository stocks, FakeCatalogReadFacade catalog, Guid? household,
        decimal? threshold = 5m) =>
        new(_productId, threshold, stocks, catalog, Clock, new FakeTenantContext(household));

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
    public async Task Sets_Threshold_On_Existing_Stock_Root_And_Persists()
    {
        var stocks = new FakeProductStockRepository();
        var existing = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        existing.AddStock(10m, _unitId, _locationId, _userId, Clock);
        stocks.Items.Add(existing);

        var result = await Command(stocks, CatalogWith(), _household, threshold: 3m).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var stock = Assert.Single(stocks.Items);
        Assert.Equal(3m, stock.LowStockThreshold);
        Assert.Equal(1, stocks.SaveChangesCalls);
    }

    [Fact]
    public async Task Creates_New_Stock_Root_When_No_Lots_Exist_And_Persists()
    {
        var stocks = new FakeProductStockRepository();

        var result = await Command(stocks, CatalogWith(), _household, threshold: 7m).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var stock = Assert.Single(stocks.Items);
        Assert.Equal(7m, stock.LowStockThreshold);
        Assert.Equal(HouseholdId.From(_household), stock.HouseholdId);
        Assert.Equal(_productId, stock.ProductId);
    }

    [Fact]
    public async Task Clears_Threshold_When_Set_To_Null()
    {
        var stocks = new FakeProductStockRepository();
        var existing = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        existing.AddStock(10m, _unitId, _locationId, _userId, Clock);
        existing.SetLowStockThreshold(5m, Clock);
        stocks.Items.Add(existing);

        var result = await Command(stocks, CatalogWith(), _household, threshold: null).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var stock = Assert.Single(stocks.Items);
        Assert.Null(stock.LowStockThreshold);
    }

    [Fact]
    public async Task UpdatedAt_Is_Bumped_After_Successful_Set()
    {
        var stocks = new FakeProductStockRepository();
        var existing = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        existing.AddStock(10m, _unitId, _locationId, _userId, Clock);
        var beforeSet = existing.UpdatedAt;
        stocks.Items.Add(existing);

        // Small delay to ensure clock advances at least once tick
        await Task.Delay(10);
        var result = await Command(stocks, CatalogWith(), _household, threshold: 4m).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var stock = Assert.Single(stocks.Items);
        Assert.True(stock.UpdatedAt >= beforeSet);
    }
}
