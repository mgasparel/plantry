using Plantry.Pantry.Application;
using Plantry.Pantry.Domain;
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

    // ── GetConsumptionStatsAsync (plantry-fuej: days-of-supply + waste rate) ─────────────────────

    /// <summary>Mutable "now" so a test can backdate journal rows outside the velocity window.</summary>
    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
    }

    private InventoryQueryService ServiceWithClock(
        FakeProductStockRepository stocks, FakeCatalogReadFacade catalog, IQuantityConverter converter, IClock clock) =>
        new(stocks, catalog, new FakeConversionProvider(converter),
            new FakeExpiringSoonHorizon(), clock, new FakeTenantContext(_household));

    [Fact(DisplayName = "plantry-fuej: returns null when the product has no stock record at all")]
    public async Task GetConsumptionStats_ReturnsNull_WhenNoStockRecord()
    {
        var stocks = new FakeProductStockRepository();
        var clock = new MutableClock();

        var stats = await ServiceWithClock(stocks, Catalog(), new IdentityQuantityConverter(), clock)
            .GetConsumptionStatsAsync(_productId);

        Assert.Null(stats);
    }

    [Fact(DisplayName = "plantry-fuej: DaysOfSupply is null with only a single Consumed event in the window (below the two-event floor)")]
    public async Task GetConsumptionStats_DaysOfSupply_Null_BelowMinimumEvents()
    {
        var stocks = new FakeProductStockRepository();
        var clock = new MutableClock();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, clock);
        stock.AddStock(100m, _grams, _location, _user, clock);
        stock.Consume(10m, _grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        stocks.Items.Add(stock);

        var stats = await ServiceWithClock(stocks, Catalog(), new IdentityQuantityConverter(), clock)
            .GetConsumptionStatsAsync(_productId);

        Assert.Null(stats?.DaysOfSupply);
    }

    [Fact(DisplayName = "plantry-fuej: DaysOfSupply = on-hand ÷ (trailing-window consumed ÷ window days)")]
    public async Task GetConsumptionStats_DaysOfSupply_Computed_From_Trailing_Window_Pace()
    {
        var stocks = new FakeProductStockRepository();
        var clock = new MutableClock();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, clock);
        stock.AddStock(1000m, _grams, _location, _user, clock);
        // Two Consumed events inside the 90-day window, totalling 180g → 2g/day pace.
        stock.Consume(90m, _grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        stock.Consume(90m, _grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        stocks.Items.Add(stock);

        var stats = await ServiceWithClock(stocks, Catalog(), new IdentityQuantityConverter(), clock)
            .GetConsumptionStatsAsync(_productId);

        Assert.NotNull(stats);
        // On hand 820g at 2g/day (180g / 90 days) = 410 days.
        Assert.Equal(410m, stats!.DaysOfSupply);
    }

    [Fact(DisplayName = "plantry-fuej: a Consumed event older than the trailing window doesn't count toward the pace")]
    public async Task GetConsumptionStats_DaysOfSupply_Excludes_Events_Outside_The_Window()
    {
        var stocks = new FakeProductStockRepository();
        var clock = new MutableClock();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, clock);
        stock.AddStock(1000m, _grams, _location, _user, clock);

        clock.UtcNow = clock.UtcNow.AddDays(-200); // well outside the 90-day window
        stock.Consume(500m, _grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        clock.UtcNow = clock.UtcNow.AddDays(200); // back to "today"
        stock.Consume(10m, _grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        stocks.Items.Add(stock);

        var stats = await ServiceWithClock(stocks, Catalog(), new IdentityQuantityConverter(), clock)
            .GetConsumptionStatsAsync(_productId);

        // Only one Consumed event falls inside the window — below the two-event floor, so null
        // rather than a pace derived from a single data point (or the excluded old one).
        Assert.Null(stats?.DaysOfSupply);
    }

    [Fact(DisplayName = "plantry-fuej: journal rows in a different unit than the product's display unit are converted before summing")]
    public async Task GetConsumptionStats_Converts_Mixed_Unit_Journal_Rows_Before_Summing()
    {
        var stocks = new FakeProductStockRepository();
        var clock = new MutableClock();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, clock);
        stock.AddStock(1m, _kilos, _location, _user, clock); // 1kg lot
        var converter = new FactorQuantityConverter(new() { [(_kilos, _grams)] = 1000m });
        // Consume from the kg lot in kg — the journal row's own UnitId is _kilos, not the product's
        // display unit (_grams) — GetConsumptionStatsAsync must convert before summing.
        stock.Consume(0.09m, _kilos, StockReason.Consumed, converter, _user, clock); // 90g
        stock.Consume(0.09m, _kilos, StockReason.Consumed, converter, _user, clock); // 90g

        stocks.Items.Add(stock);

        var stats = await ServiceWithClock(stocks, Catalog(), converter, clock)
            .GetConsumptionStatsAsync(_productId);

        Assert.NotNull(stats);
        // On hand: 1000g - 180g = 820g, at 2g/day pace (180g / 90 days) = 410 days — same figure as
        // the identity-converter test, proving the mixed-unit journal rows converted correctly.
        Assert.Equal(410m, stats!.DaysOfSupply);
    }

    [Fact(DisplayName = "plantry-fuej: WasteRate is null when the product has no Consumed or Discarded history")]
    public async Task GetConsumptionStats_WasteRate_Null_WithNoRemovalHistory()
    {
        var stocks = new FakeProductStockRepository();
        var clock = new MutableClock();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, clock);
        stock.AddStock(100m, _grams, _location, _user, clock);
        stocks.Items.Add(stock);

        var stats = await ServiceWithClock(stocks, Catalog(), new IdentityQuantityConverter(), clock)
            .GetConsumptionStatsAsync(_productId);

        Assert.Null(stats);
    }

    [Fact(DisplayName = "plantry-fuej: WasteRate = discarded ÷ (discarded + consumed), across the product's whole history")]
    public async Task GetConsumptionStats_WasteRate_Computed_Across_Full_History()
    {
        var stocks = new FakeProductStockRepository();
        var clock = new MutableClock();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, clock);
        stock.AddStock(100m, _grams, _location, _user, clock);
        stock.Consume(60m, _grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        stock.Consume(20m, _grams, StockReason.Discarded, new IdentityQuantityConverter(), _user, clock);
        stocks.Items.Add(stock);

        var stats = await ServiceWithClock(stocks, Catalog(), new IdentityQuantityConverter(), clock)
            .GetConsumptionStatsAsync(_productId);

        Assert.NotNull(stats);
        // 20 discarded / (20 discarded + 60 consumed) = 0.25.
        Assert.Equal(0.25m, stats!.WasteRate);
    }
}
