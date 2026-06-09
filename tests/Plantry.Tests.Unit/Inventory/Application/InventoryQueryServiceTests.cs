using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Inventory.Application;

public sealed class InventoryQueryServiceTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _productId = Guid.CreateVersion7();
    private readonly Guid _grams = Guid.CreateVersion7();
    private readonly Guid _kilos = Guid.CreateVersion7();
    private readonly Guid _location = Guid.CreateVersion7();
    private readonly Guid _user = Guid.CreateVersion7();

    private InventoryQueryService Service(
        FakeProductStockRepository stocks, FakeCatalogReadFacade catalog, IQuantityConverter converter, Guid? household) =>
        new(stocks, catalog, new FakeConversionProvider(converter), Clock, new FakeTenantContext(household));

    private FakeCatalogReadFacade Catalog()
    {
        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(_productId, "Flour", "Baking", _grams, "g", CanHoldStock: true));
        catalog.UnitCodes[_grams] = "g";
        catalog.UnitCodes[_kilos] = "kg";
        catalog.LocationNames[_location] = "Pantry";
        return catalog;
    }

    [Fact]
    public async Task ListPantry_Aggregates_Across_Lots_In_The_Display_Unit()
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        stock.AddStock(500m, _grams, _location, _user, Clock);          // 500 g
        stock.AddStock(2m, _kilos, _location, _user, Clock);            // 2 kg = 2000 g
        stocks.Items.Add(stock);

        var converter = new FactorQuantityConverter(new() { [(_kilos, _grams)] = 1000m });
        var pantry = await Service(stocks, Catalog(), converter, _household).ListPantryAsync();

        var item = Assert.Single(pantry);
        Assert.Equal("Flour", item.Name);
        Assert.Equal("Baking", item.CategoryName);
        Assert.Equal(2500m, item.TotalQuantity);
        Assert.Equal("g", item.DisplayUnitCode);
        Assert.Equal(2, item.LotCount);
    }

    [Fact]
    public async Task ListPantry_Skips_Products_Whose_Lots_Are_All_Depleted()
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        stock.AddStock(10m, _grams, _location, _user, Clock);
        stock.Consume(10m, _grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, Clock);
        stocks.Items.Add(stock);

        var pantry = await Service(stocks, Catalog(), new IdentityQuantityConverter(), _household).ListPantryAsync();

        Assert.Empty(pantry);
    }

    [Theory]
    [InlineData(-1, ExpiryTone.Expired)]
    [InlineData(3, ExpiryTone.Soon)]
    [InlineData(60, ExpiryTone.Ok)]
    public async Task ListPantry_Computes_Expiry_Tone_From_Soonest_Lot(int daysFromToday, ExpiryTone expected)
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        stock.AddStock(100m, _grams, _location, _user, Clock, expiryDate: Today.AddDays(daysFromToday));
        stock.AddStock(100m, _grams, _location, _user, Clock, expiryDate: Today.AddDays(daysFromToday + 90));
        stocks.Items.Add(stock);

        var pantry = await Service(stocks, Catalog(), new IdentityQuantityConverter(), _household).ListPantryAsync();

        var item = Assert.Single(pantry);
        Assert.Equal(Today.AddDays(daysFromToday), item.SoonestExpiry);
        Assert.Equal(expected, item.ExpiryTone);
    }

    [Fact]
    public async Task FindDetail_Returns_Live_Lots_And_Journal_History_Newest_First()
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        stock.AddStock(100m, _grams, _location, _user, Clock, expiryDate: Today.AddDays(10), purchasedAt: Today);
        stock.Consume(40m, _grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, Clock);
        stocks.Items.Add(stock);

        var detail = await Service(stocks, Catalog(), new IdentityQuantityConverter(), _household).FindDetailAsync(_productId);

        Assert.NotNull(detail);
        Assert.Equal("Flour", detail!.Name);
        Assert.Equal(60m, detail.TotalQuantity);

        var lot = Assert.Single(detail.Lots);
        Assert.Equal(60m, lot.Quantity);
        Assert.Equal("g", lot.UnitCode);
        Assert.Equal("Pantry", lot.LocationName);

        Assert.Equal(2, detail.History.Count);
        Assert.Equal(StockReason.Consumed, detail.History[0].Reason); // newest first
        Assert.Equal(-40m, detail.History[0].Delta);
        Assert.Equal(StockReason.Purchase, detail.History[1].Reason);
    }

    [Fact]
    public async Task FindDetail_Returns_Null_When_No_Household_In_Context()
    {
        var stocks = new FakeProductStockRepository();

        var detail = await Service(stocks, Catalog(), new IdentityQuantityConverter(), household: null).FindDetailAsync(_productId);

        Assert.Null(detail);
    }
}
