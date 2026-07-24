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
        FakeProductStockRepository stocks, FakeCatalogReadFacade catalog, IQuantityConverter converter, Guid? household,
        int horizonDays = HouseholdInventorySettings.DefaultExpiringSoonDays) =>
        new(stocks, catalog, new FakeConversionProvider(converter),
            new FakeExpiringSoonHorizon(horizonDays), Clock, new FakeTenantContext(household));

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

    [Fact(DisplayName = "plantry-lxm2: archiving a product never hides its still-active stock from the In stock scope")]
    public async Task ListPantry_Includes_Archived_Product_With_Active_Stock()
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        stock.AddStock(500m, _grams, _location, _user, Clock);
        stocks.Items.Add(stock);

        var catalog = new FakeCatalogReadFacade();
        catalog.ArchivedProducts.Add(new CatalogProductInfo(_productId, "Instant espresso", "Beverages", _grams, "g", CanHoldStock: true, IsArchived: true));
        catalog.UnitCodes[_grams] = "g";
        catalog.LocationNames[_location] = "Pantry";

        var pantry = await Service(stocks, catalog, new IdentityQuantityConverter(), _household).ListPantryAsync();

        var item = Assert.Single(pantry);
        Assert.Equal("Instant espresso", item.Name);
        Assert.Equal(500m, item.TotalQuantity);
        Assert.True(item.IsArchived);
        Assert.True(item.IsStocked);
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

    [Fact(DisplayName = "ExpiryTone.Soon boundary follows the configured horizon (3 days), not the default")]
    public async Task ListPantry_ExpiryTone_Soon_Honors_Configured_Horizon()
    {
        // With a configured horizon of 3, a lot 3 days out is Soon and one 4 days out is Ok.
        // Under the default (7) both would be Soon — so this passes only if the configured value is read.
        var withinId = _productId;
        var beyondId = Guid.CreateVersion7();

        var catalog = Catalog();
        catalog.Products.Add(new CatalogProductInfo(beyondId, "Sugar", "Baking", _grams, "g", CanHoldStock: true));

        var stocks = new FakeProductStockRepository();
        var within = ProductStock.Start(HouseholdId.From(_household), withinId, Clock);
        within.AddStock(100m, _grams, _location, _user, Clock, expiryDate: Today.AddDays(3));
        var beyond = ProductStock.Start(HouseholdId.From(_household), beyondId, Clock);
        beyond.AddStock(100m, _grams, _location, _user, Clock, expiryDate: Today.AddDays(4));
        stocks.Items.Add(within);
        stocks.Items.Add(beyond);

        var pantry = await Service(stocks, catalog, new IdentityQuantityConverter(), _household, horizonDays: 3)
            .ListPantryAsync();

        Assert.Equal(ExpiryTone.Soon, pantry.Single(i => i.ProductId == withinId).ExpiryTone);
        Assert.Equal(ExpiryTone.Ok, pantry.Single(i => i.ProductId == beyondId).ExpiryTone);
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

    [Fact]
    public async Task ListPantry_Returns_Empty_When_No_Household_In_Context()
    {
        var stocks = new FakeProductStockRepository();

        var pantry = await Service(stocks, Catalog(), new IdentityQuantityConverter(), household: null).ListPantryAsync();

        Assert.Empty(pantry);
    }

    [Fact]
    public async Task ListPantry_Skips_Products_Missing_From_Catalog()
    {
        var orphanProductId = Guid.CreateVersion7();
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), orphanProductId, Clock);
        stock.AddStock(100m, _grams, _location, _user, Clock);
        stocks.Items.Add(stock);

        var pantry = await Service(stocks, new FakeCatalogReadFacade(), new IdentityQuantityConverter(), _household).ListPantryAsync();

        Assert.Empty(pantry);
    }

    [Fact]
    public async Task ListPantry_Shows_Multiple_When_Lots_Span_Different_Locations()
    {
        var secondLocation = Guid.CreateVersion7();
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        stock.AddStock(100m, _grams, _location, _user, Clock);
        stock.AddStock(100m, _grams, secondLocation, _user, Clock);
        stocks.Items.Add(stock);

        var pantry = await Service(stocks, Catalog(), new IdentityQuantityConverter(), _household).ListPantryAsync();

        var item = Assert.Single(pantry);
        Assert.Equal("Multiple", item.LocationDisplay);
    }

    [Fact]
    public async Task ListPantry_Sets_ExpiryTone_None_When_No_Lots_Have_Dates()
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        stock.AddStock(100m, _grams, _location, _user, Clock, expiryDate: null);
        stocks.Items.Add(stock);

        var pantry = await Service(stocks, Catalog(), new IdentityQuantityConverter(), _household).ListPantryAsync();

        var item = Assert.Single(pantry);
        Assert.Equal(ExpiryTone.None, item.ExpiryTone);
        Assert.Null(item.SoonestExpiry);
    }

    [Fact]
    public async Task ListPantry_Falls_Back_To_Lot_Unit_When_Conversion_To_Display_Unit_Fails()
    {
        var ea = Guid.CreateVersion7();
        var catalog = Catalog();
        catalog.UnitCodes[ea] = "ea";

        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        stock.AddStock(3m, ea, _location, _user, Clock);   // product default is "g"; lots are "ea"
        stocks.Items.Add(stock);

        // FactorQuantityConverter with no factors → ea→g fails, total stays 0 → fallback triggers
        var converter = new FactorQuantityConverter([]);
        var pantry = await Service(stocks, catalog, converter, _household).ListPantryAsync();

        var item = Assert.Single(pantry);
        Assert.Equal(3m, item.TotalQuantity);
        Assert.Equal("ea", item.DisplayUnitCode);
    }

    [Fact(DisplayName = "FindDetail returns a zero-lot empty detail when the product exists in the catalog "
        + "but has never been stocked (plantry-sjfn) — the Pantry \"Everything\" scope links catalog-only "
        + "products straight here, so this must not 404")]
    public async Task FindDetail_Returns_ZeroStock_Detail_When_Never_Stocked()
    {
        var stocks = new FakeProductStockRepository();

        var detail = await Service(stocks, Catalog(), new IdentityQuantityConverter(), _household).FindDetailAsync(_productId);

        Assert.NotNull(detail);
        Assert.Equal("Flour", detail!.Name);
        Assert.Equal("Baking", detail.CategoryName);
        Assert.Equal("g", detail.DisplayUnitCode);
        Assert.Equal(0m, detail.TotalQuantity);
        Assert.Empty(detail.Lots);
        Assert.Empty(detail.History);
        Assert.Null(detail.LowStockThreshold);
        Assert.False(detail.IsRunningLow);
    }

    [Fact(DisplayName = "FindDetail still returns null when the product doesn't exist in the catalog at all "
        + "— a stale/removed id genuinely 404s")]
    public async Task FindDetail_Returns_Null_When_Product_Not_In_Catalog()
    {
        var stocks = new FakeProductStockRepository();
        var emptyCatalog = new FakeCatalogReadFacade(); // no products registered

        var detail = await Service(stocks, emptyCatalog, new IdentityQuantityConverter(), _household).FindDetailAsync(_productId);

        Assert.Null(detail);
    }

    [Fact]
    public async Task FindDetail_Shows_Unknown_Product_Name_And_Question_Mark_Unit_When_Not_In_Catalog()
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        stock.AddStock(100m, _grams, _location, _user, Clock);
        stocks.Items.Add(stock);

        var detail = await Service(stocks, new FakeCatalogReadFacade(), new IdentityQuantityConverter(), _household)
            .FindDetailAsync(_productId);

        Assert.NotNull(detail);
        Assert.Equal("Unknown product", detail!.Name);
        Assert.Equal("?", detail.DisplayUnitCode);
        Assert.Equal(0m, detail.TotalQuantity);
    }

    // ── LowStockThreshold / IsRunningLow surfaced via ListPantry ──────────

    [Fact]
    public async Task ListPantry_Surfaces_LowStockThreshold_And_IsRunningLow_True_When_OnHand_At_Or_Below_Threshold()
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        stock.AddStock(4m, _grams, _location, _user, Clock);
        stock.SetLowStockThreshold(5m, Clock); // 4 ≤ 5 → running low
        stocks.Items.Add(stock);

        var pantry = await Service(stocks, Catalog(), new IdentityQuantityConverter(), _household).ListPantryAsync();

        var item = Assert.Single(pantry);
        Assert.Equal(5m, item.LowStockThreshold);
        Assert.True(item.IsRunningLow);
    }

    [Fact]
    public async Task ListPantry_Surfaces_IsRunningLow_False_When_OnHand_Above_Threshold()
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        stock.AddStock(10m, _grams, _location, _user, Clock);
        stock.SetLowStockThreshold(5m, Clock); // 10 > 5 → not running low
        stocks.Items.Add(stock);

        var pantry = await Service(stocks, Catalog(), new IdentityQuantityConverter(), _household).ListPantryAsync();

        var item = Assert.Single(pantry);
        Assert.Equal(5m, item.LowStockThreshold);
        Assert.False(item.IsRunningLow);
    }

    [Fact]
    public async Task ListPantry_Surfaces_IsRunningLow_False_When_No_Threshold_Set()
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        stock.AddStock(1m, _grams, _location, _user, Clock);
        // no threshold set
        stocks.Items.Add(stock);

        var pantry = await Service(stocks, Catalog(), new IdentityQuantityConverter(), _household).ListPantryAsync();

        var item = Assert.Single(pantry);
        Assert.Null(item.LowStockThreshold);
        Assert.False(item.IsRunningLow);
    }

    // ── LowStockThreshold / IsRunningLow surfaced via FindDetail ──────────

    [Fact]
    public async Task FindDetail_Surfaces_LowStockThreshold_And_IsRunningLow_True_When_OnHand_At_Threshold()
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        stock.AddStock(5m, _grams, _location, _user, Clock);
        stock.SetLowStockThreshold(5m, Clock); // exactly at threshold → running low
        stocks.Items.Add(stock);

        var detail = await Service(stocks, Catalog(), new IdentityQuantityConverter(), _household).FindDetailAsync(_productId);

        Assert.NotNull(detail);
        Assert.Equal(5m, detail!.LowStockThreshold);
        Assert.True(detail.IsRunningLow);
    }

    [Fact]
    public async Task FindDetail_Surfaces_IsRunningLow_False_When_No_Threshold_Set()
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        stock.AddStock(100m, _grams, _location, _user, Clock);
        stocks.Items.Add(stock);

        var detail = await Service(stocks, Catalog(), new IdentityQuantityConverter(), _household).FindDetailAsync(_productId);

        Assert.NotNull(detail);
        Assert.Null(detail!.LowStockThreshold);
        Assert.False(detail.IsRunningLow);
    }
}
