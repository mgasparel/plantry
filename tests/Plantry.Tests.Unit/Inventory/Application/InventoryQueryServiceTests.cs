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
        int horizonDays = HouseholdInventorySettings.DefaultExpiringSoonDays, FakeLowStockRuleRepository? rules = null)
    {
        var conversions = new FakeConversionProvider(converter);
        var tenant = new FakeTenantContext(household);
        return new(stocks, rules ?? new FakeLowStockRuleRepository(), catalog, conversions,
            new FakeExpiringSoonHorizon(horizonDays), Clock, tenant,
            new OnHandRollupQuery(stocks, catalog, conversions, tenant));
    }

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
    public async Task ListPantry_Projects_IsProduced_From_Catalog_Product()
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        stock.AddStock(100m, _grams, _location, _user, Clock);
        stocks.Items.Add(stock);
        var catalog = Catalog();
        catalog.Products.Clear();
        catalog.Products.Add(new CatalogProductInfo(_productId, "Homemade soup", "Meals", _grams, "g", CanHoldStock: true, IsProduced: true));

        var item = Assert.Single(await Service(stocks, catalog, new IdentityQuantityConverter(), _household).ListPantryAsync());

        Assert.True(item.IsProduced);
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
        stocks.Items.Add(stock);
        var rules = new FakeLowStockRuleRepository();
        rules.Items.Add(LowStockRule.Create(HouseholdId.From(_household), _productId, 5m, Clock)); // 4 ≤ 5 → running low

        var pantry = await Service(stocks, Catalog(), new IdentityQuantityConverter(), _household, rules: rules).ListPantryAsync();

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
        stocks.Items.Add(stock);
        var rules = new FakeLowStockRuleRepository();
        rules.Items.Add(LowStockRule.Create(HouseholdId.From(_household), _productId, 5m, Clock)); // 10 > 5 → not running low

        var pantry = await Service(stocks, Catalog(), new IdentityQuantityConverter(), _household, rules: rules).ListPantryAsync();

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
        stocks.Items.Add(stock);
        var rules = new FakeLowStockRuleRepository();
        rules.Items.Add(LowStockRule.Create(HouseholdId.From(_household), _productId, 5m, Clock)); // exactly at threshold → running low

        var detail = await Service(stocks, Catalog(), new IdentityQuantityConverter(), _household, rules: rules).FindDetailAsync(_productId);

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

    // ── Parent product detail / pantry list (plantry-oh27.7) ─────────────

    [Fact]
    public async Task FindDetail_Parent_Aggregates_OnHand_Across_Live_Variants_With_Breakdown()
    {
        var parentId = Guid.CreateVersion7();
        var variant1 = Guid.CreateVersion7();
        var variant2 = Guid.CreateVersion7();

        var stocks = new FakeProductStockRepository();
        var stock1 = ProductStock.Start(HouseholdId.From(_household), variant1, Clock);
        stock1.AddStock(4m, _grams, _location, _user, Clock);
        var stock2 = ProductStock.Start(HouseholdId.From(_household), variant2, Clock);
        stock2.AddStock(6m, _grams, _location, _user, Clock);
        stocks.Items.Add(stock1);
        stocks.Items.Add(stock2);

        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(parentId, "Bubly", "Drinks", _grams, "g", CanHoldStock: false));
        catalog.Products.Add(new CatalogProductInfo(variant1, "Bubly Lime", "Drinks", _grams, "g", CanHoldStock: true, ParentProductId: parentId));
        catalog.Products.Add(new CatalogProductInfo(variant2, "Bubly Grapefruit", "Drinks", _grams, "g", CanHoldStock: true, ParentProductId: parentId));
        catalog.UnitCodes[_grams] = "g";

        var detail = await Service(stocks, catalog, new IdentityQuantityConverter(), _household).FindDetailAsync(parentId);

        Assert.NotNull(detail);
        Assert.Equal(10m, detail!.TotalQuantity);
        Assert.Empty(detail.Lots);
        Assert.Empty(detail.History);
        Assert.Equal(2, detail.Variants.Count);
        Assert.Contains(detail.Variants, v => v.VariantId == variant1 && v.Name == "Bubly Lime" && v.OnHand == 4m);
        Assert.Contains(detail.Variants, v => v.VariantId == variant2 && v.Name == "Bubly Grapefruit" && v.OnHand == 6m);
        Assert.Empty(detail.UnconvertedVariantIds);
    }

    [Fact]
    public async Task FindDetail_Parent_Excludes_Unconverted_Variant_From_Total_But_Lists_It()
    {
        var parentId = Guid.CreateVersion7();
        var convertibleVariant = Guid.CreateVersion7();
        var unconvertibleVariant = Guid.CreateVersion7();
        var otherUnit = Guid.CreateVersion7();

        var stocks = new FakeProductStockRepository();
        var stock1 = ProductStock.Start(HouseholdId.From(_household), convertibleVariant, Clock);
        stock1.AddStock(4m, _grams, _location, _user, Clock);
        var stock2 = ProductStock.Start(HouseholdId.From(_household), unconvertibleVariant, Clock);
        stock2.AddStock(2m, otherUnit, _location, _user, Clock);
        stocks.Items.Add(stock1);
        stocks.Items.Add(stock2);

        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(parentId, "Bubly", "Drinks", _grams, "g", CanHoldStock: false));
        catalog.Products.Add(new CatalogProductInfo(convertibleVariant, "Bubly Lime", "Drinks", _grams, "g", CanHoldStock: true, ParentProductId: parentId));
        catalog.Products.Add(new CatalogProductInfo(unconvertibleVariant, "Bubly Weird", "Drinks", otherUnit, "?", CanHoldStock: true, ParentProductId: parentId));
        catalog.UnitCodes[_grams] = "g";

        // No conversion registered between otherUnit and _grams — FactorQuantityConverter fails closed.
        var converter = new FactorQuantityConverter(new Dictionary<(Guid, Guid), decimal>());
        var detail = await Service(stocks, catalog, converter, _household).FindDetailAsync(parentId);

        Assert.NotNull(detail);
        Assert.Equal(4m, detail!.TotalQuantity); // only the convertible variant contributes
        Assert.Equal(2, detail.Variants.Count); // both still listed, never silently dropped
        Assert.Equal([unconvertibleVariant], detail.UnconvertedVariantIds);
        // IsLow reflects the variant's OWN on-hand (2, in its own unrelated unit) — a failed conversion
        // is a display concern (UnconvertedVariantIds), never mistaken for "out of stock".
        Assert.False(detail.Variants.Single(v => v.VariantId == unconvertibleVariant).IsLow);
    }

    [Fact]
    public async Task FindDetail_Parent_IsRunningLow_Against_Aggregate_Using_Parent_Rule()
    {
        var parentId = Guid.CreateVersion7();
        var variant1 = Guid.CreateVersion7();

        var stocks = new FakeProductStockRepository();
        var stock1 = ProductStock.Start(HouseholdId.From(_household), variant1, Clock);
        stock1.AddStock(3m, _grams, _location, _user, Clock);
        stocks.Items.Add(stock1);

        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(parentId, "Bubly", "Drinks", _grams, "g", CanHoldStock: false));
        catalog.Products.Add(new CatalogProductInfo(variant1, "Bubly Lime", "Drinks", _grams, "g", CanHoldStock: true, ParentProductId: parentId));
        catalog.UnitCodes[_grams] = "g";

        var rules = new FakeLowStockRuleRepository();
        rules.Items.Add(LowStockRule.Create(HouseholdId.From(_household), parentId, 5m, Clock)); // 3 <= 5

        var detail = await Service(stocks, catalog, new IdentityQuantityConverter(), _household, rules: rules).FindDetailAsync(parentId);

        Assert.NotNull(detail);
        Assert.Equal(5m, detail!.LowStockThreshold);
        Assert.True(detail.IsRunningLow);
    }

    [Fact]
    public async Task ListPantry_Includes_Parent_With_Rule_Even_When_No_Variant_Is_Stocked()
    {
        var parentId = Guid.CreateVersion7();
        var variant1 = Guid.CreateVersion7();

        var stocks = new FakeProductStockRepository(); // nothing stocked at all
        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(parentId, "Bubly", "Drinks", _grams, "g", CanHoldStock: false));
        catalog.Products.Add(new CatalogProductInfo(variant1, "Bubly Lime", "Drinks", _grams, "g", CanHoldStock: true, ParentProductId: parentId));
        catalog.UnitCodes[_grams] = "g";

        var rules = new FakeLowStockRuleRepository();
        rules.Items.Add(LowStockRule.Create(HouseholdId.From(_household), parentId, 5m, Clock));

        var items = await Service(stocks, catalog, new IdentityQuantityConverter(), _household, rules: rules).ListPantryAsync();

        var parentRow = Assert.Single(items, i => i.ProductId == parentId);
        Assert.True(parentRow.IsParent);
        Assert.Equal(0m, parentRow.TotalQuantity);
        Assert.Equal(5m, parentRow.LowStockThreshold);
        Assert.True(parentRow.IsRunningLow); // 0 <= 5
    }

    [Fact]
    public async Task ListPantry_Includes_Parent_With_At_Least_One_Stocked_Live_Variant()
    {
        var parentId = Guid.CreateVersion7();
        var variant1 = Guid.CreateVersion7();

        var stocks = new FakeProductStockRepository();
        var stock1 = ProductStock.Start(HouseholdId.From(_household), variant1, Clock);
        stock1.AddStock(9m, _grams, _location, _user, Clock);
        stocks.Items.Add(stock1);

        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(parentId, "Bubly", "Drinks", _grams, "g", CanHoldStock: false));
        catalog.Products.Add(new CatalogProductInfo(variant1, "Bubly Lime", "Drinks", _grams, "g", CanHoldStock: true, ParentProductId: parentId));
        catalog.UnitCodes[_grams] = "g";

        var items = await Service(stocks, catalog, new IdentityQuantityConverter(), _household).ListPantryAsync();

        var parentRow = Assert.Single(items, i => i.ProductId == parentId);
        Assert.True(parentRow.IsParent);
        Assert.Equal(9m, parentRow.TotalQuantity);
    }

    [Fact]
    public async Task ListPantry_Excludes_Depleted_Shell_Parent_With_No_Rule()
    {
        var parentId = Guid.CreateVersion7();
        var variant1 = Guid.CreateVersion7();

        var stocks = new FakeProductStockRepository();
        var stock1 = ProductStock.Start(HouseholdId.From(_household), variant1, Clock);
        stock1.AddStock(9m, _grams, _location, _user, Clock);
        stock1.Consume(9m, _grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, Clock);
        stocks.Items.Add(stock1); // the ProductStock row survives past its last lot — a "depleted shell"

        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(parentId, "Bubly", "Drinks", _grams, "g", CanHoldStock: false));
        catalog.Products.Add(new CatalogProductInfo(variant1, "Bubly Lime", "Drinks", _grams, "g", CanHoldStock: true, ParentProductId: parentId));
        catalog.UnitCodes[_grams] = "g";

        var items = await Service(stocks, catalog, new IdentityQuantityConverter(), _household).ListPantryAsync();

        Assert.DoesNotContain(items, i => i.ProductId == parentId);
    }

    [Fact]
    public async Task ListPantry_Includes_Depleted_Shell_Parent_With_Rule_But_Marks_Not_Stocked()
    {
        var parentId = Guid.CreateVersion7();
        var variant1 = Guid.CreateVersion7();

        var stocks = new FakeProductStockRepository();
        var stock1 = ProductStock.Start(HouseholdId.From(_household), variant1, Clock);
        stock1.AddStock(9m, _grams, _location, _user, Clock);
        stock1.Consume(9m, _grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, Clock);
        stocks.Items.Add(stock1); // depleted shell, but the parent still carries a household rule

        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(parentId, "Bubly", "Drinks", _grams, "g", CanHoldStock: false));
        catalog.Products.Add(new CatalogProductInfo(variant1, "Bubly Lime", "Drinks", _grams, "g", CanHoldStock: true, ParentProductId: parentId));
        catalog.UnitCodes[_grams] = "g";

        var rules = new FakeLowStockRuleRepository();
        rules.Items.Add(LowStockRule.Create(HouseholdId.From(_household), parentId, 5m, Clock));

        var items = await Service(stocks, catalog, new IdentityQuantityConverter(), _household, rules: rules).ListPantryAsync();

        var parentRow = Assert.Single(items, i => i.ProductId == parentId);
        Assert.True(parentRow.IsParent);
        Assert.False(parentRow.IsStocked);
        Assert.Equal(0m, parentRow.TotalQuantity);
        Assert.True(parentRow.IsRunningLow); // 0 <= 5
    }

    // ── GetConsumptionStatsAsync (plantry-fuej: days-of-supply + waste rate) ─────────────────────

    /// <summary>Mutable "now" so a test can backdate journal rows outside the velocity window.</summary>
    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
    }

    private InventoryQueryService ServiceWithClock(
        FakeProductStockRepository stocks, FakeCatalogReadFacade catalog, IQuantityConverter converter, IClock clock)
    {
        var conversions = new FakeConversionProvider(converter);
        var tenant = new FakeTenantContext(_household);
        return new(stocks, new FakeLowStockRuleRepository(), catalog, conversions,
            new FakeExpiringSoonHorizon(), clock, tenant,
            new OnHandRollupQuery(stocks, catalog, conversions, tenant));
    }

    private InventoryQueryService ServiceWithConversions(
        FakeProductStockRepository stocks, FakeCatalogReadFacade catalog,
        IProductConversionProvider conversions, IClock clock, Guid? household = null)
    {
        var tenant = new FakeTenantContext(household ?? _household);
        return new(stocks, new FakeLowStockRuleRepository(), catalog, conversions,
            new FakeExpiringSoonHorizon(), clock, tenant,
            new OnHandRollupQuery(stocks, catalog, conversions, tenant));
    }

    [Fact(DisplayName = "plantry-ikul: parent consumption stats pool quantities with each variant's converter")]
    public async Task GetConsumptionStats_Parent_PoolsConvertedQuantitiesAndExcludesArchivedAndUnrelatedProducts()
    {
        var parentId = Guid.CreateVersion7();
        var variantA = Guid.CreateVersion7();
        var variantB = Guid.CreateVersion7();
        var archivedVariant = Guid.CreateVersion7();
        var unrelatedProduct = Guid.CreateVersion7();
        var grams = Guid.CreateVersion7();
        var pack = Guid.CreateVersion7();
        var unrelatedUnit = Guid.CreateVersion7();
        var clock = new MutableClock();
        var converterA = new FactorQuantityConverter(new() { [(pack, grams)] = 250m });
        var converterB = new FactorQuantityConverter(new() { [(pack, grams)] = 1000m });

        var stockA = ProductStock.Start(HouseholdId.From(_household), variantA, clock);
        clock.UtcNow = clock.UtcNow.AddDays(-30);
        stockA.AddStock(4m, pack, _location, _user, clock);
        clock.UtcNow = clock.UtcNow.AddDays(30);
        stockA.Consume(1m, pack, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        stockA.Consume(1m, pack, StockReason.Discarded, new IdentityQuantityConverter(), _user, clock);

        var stockB = ProductStock.Start(HouseholdId.From(_household), variantB, clock);
        clock.UtcNow = clock.UtcNow.AddDays(-7);
        stockB.AddStock(2m, pack, _location, _user, clock);
        clock.UtcNow = clock.UtcNow.AddDays(7);
        stockB.Consume(0.5m, pack, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        stockB.Consume(0.1m, pack, StockReason.Discarded, new IdentityQuantityConverter(), _user, clock);

        // These roots must not leak into the parent pool, even if a test facade returns archived
        // rows through ListProductsAsync or the parent itself happens to have a stock shell.
        var archivedStock = ProductStock.Start(HouseholdId.From(_household), archivedVariant, clock);
        archivedStock.AddStock(500m, unrelatedUnit, _location, _user, clock);
        var unrelatedStock = ProductStock.Start(HouseholdId.From(_household), unrelatedProduct, clock);
        unrelatedStock.AddStock(900m, unrelatedUnit, _location, _user, clock);
        var parentStock = ProductStock.Start(HouseholdId.From(_household), parentId, clock);
        parentStock.AddStock(4000m, grams, _location, _user, clock);

        var stocks = new FakeProductStockRepository();
        stocks.Items.AddRange([stockA, stockB, archivedStock, unrelatedStock, parentStock]);
        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(parentId, "Bubly", "Drinks", grams, "g", CanHoldStock: false));
        catalog.Products.Add(new CatalogProductInfo(variantA, "Lime", "Drinks", pack, "pack", CanHoldStock: true, ParentProductId: parentId));
        catalog.Products.Add(new CatalogProductInfo(variantB, "Grapefruit", "Drinks", pack, "pack", CanHoldStock: true, ParentProductId: parentId));
        catalog.Products.Add(new CatalogProductInfo(archivedVariant, "Archived", "Drinks", unrelatedUnit, "?", CanHoldStock: true, ParentProductId: parentId, IsArchived: true));
        catalog.Products.Add(new CatalogProductInfo(unrelatedProduct, "Unrelated", "Drinks", unrelatedUnit, "?", CanHoldStock: true));
        var conversions = new PerProductConversionProvider(new Dictionary<Guid, IQuantityConverter>
        {
            [variantA] = converterA,
            [variantB] = converterB,
        });

        var stats = await ServiceWithConversions(stocks, catalog, conversions, clock)
            .GetConsumptionStatsAsync(parentId);

        Assert.NotNull(stats);
        Assert.Equal(76m, stats!.DaysOfSupply); // 1,900g ÷ (750g / 30d)
        Assert.Equal(350m / 1100m, stats.WasteRate); // quantity weighted; average variant rates would be 33.3%
    }

    [Fact(DisplayName = "plantry-ikul: parent returns no chips without live variant removal history")]
    public async Task GetConsumptionStats_Parent_ReturnsNullWithoutContributorsOrRemovalHistory()
    {
        var parentId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();
        var grams = Guid.CreateVersion7();
        var clock = new MutableClock();
        var stocks = new FakeProductStockRepository();
        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(parentId, "Parent", null, grams, "g", CanHoldStock: false));

        var service = ServiceWithClock(stocks, catalog, new IdentityQuantityConverter(), clock);
        Assert.Null(await service.GetConsumptionStatsAsync(parentId)); // no direct variants

        catalog.Products.Add(new CatalogProductInfo(variantId, "Archived", null, grams, "g",
            CanHoldStock: true, ParentProductId: parentId, IsArchived: true));
        var archivedStock = ProductStock.Start(HouseholdId.From(_household), variantId, clock);
        archivedStock.AddStock(10m, grams, _location, _user, clock);
        stocks.Items.Add(archivedStock);
        Assert.Null(await service.GetConsumptionStatsAsync(parentId)); // archived variants are excluded

        catalog.Products.RemoveAt(catalog.Products.Count - 1);
        stocks.Items.Remove(archivedStock);
        catalog.Products.Add(new CatalogProductInfo(variantId, "Live", null, grams, "g",
            CanHoldStock: true, ParentProductId: parentId));
        Assert.Null(await service.GetConsumptionStatsAsync(parentId)); // variant has no stock aggregate

        var liveStock = ProductStock.Start(HouseholdId.From(_household), variantId, clock);
        liveStock.AddStock(10m, grams, _location, _user, clock); // Purchase alone is neither consumption nor waste
        stocks.Items.Add(liveStock);
        Assert.Null(await service.GetConsumptionStatsAsync(parentId));
    }

    [Fact(DisplayName = "plantry-ikul: depleted variant history contributes to pooled waste")]
    public async Task GetConsumptionStats_Parent_IncludesDepletedVariantHistory()
    {
        var parentId = Guid.CreateVersion7();
        var liveId = Guid.CreateVersion7();
        var depletedId = Guid.CreateVersion7();
        var grams = Guid.CreateVersion7();
        var clock = new MutableClock();
        var stocks = new FakeProductStockRepository();
        var live = ProductStock.Start(HouseholdId.From(_household), liveId, clock);
        live.AddStock(2m, grams, _location, _user, clock);
        live.Consume(1m, grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        live.Consume(1m, grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        var depleted = ProductStock.Start(HouseholdId.From(_household), depletedId, clock);
        depleted.AddStock(10m, grams, _location, _user, clock);
        depleted.Consume(10m, grams, StockReason.Discarded, new IdentityQuantityConverter(), _user, clock);
        Assert.Empty(depleted.ActiveLotsFefo());
        stocks.Items.AddRange([live, depleted]);

        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(parentId, "Parent", null, grams, "g", CanHoldStock: false));
        catalog.Products.Add(new CatalogProductInfo(liveId, "Live", null, grams, "g", CanHoldStock: true, ParentProductId: parentId));
        catalog.Products.Add(new CatalogProductInfo(depletedId, "Depleted", null, grams, "g", CanHoldStock: true, ParentProductId: parentId));

        var stats = await ServiceWithClock(stocks, catalog, new IdentityQuantityConverter(), clock)
            .GetConsumptionStatsAsync(parentId);

        Assert.NotNull(stats);
        Assert.Equal(0m, stats!.DaysOfSupply);
        Assert.Equal(10m / 12m, stats.WasteRate);
    }

    [Fact(DisplayName = "plantry-ikul: parent stats convert mixed kg and g stock and journal quantities")]
    public async Task GetConsumptionStats_Parent_ConvertsMixedDefaultAndJournalUnits()
    {
        var parentId = Guid.CreateVersion7();
        var gramsVariant = Guid.CreateVersion7();
        var kilosVariant = Guid.CreateVersion7();
        var grams = Guid.CreateVersion7();
        var kilos = Guid.CreateVersion7();
        var clock = new MutableClock();
        var stockA = ProductStock.Start(HouseholdId.From(_household), gramsVariant, clock);
        clock.UtcNow = clock.UtcNow.AddDays(-30);
        stockA.AddStock(100m, grams, _location, _user, clock);
        clock.UtcNow = clock.UtcNow.AddDays(30);
        stockA.Consume(10m, grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        stockA.Consume(10m, grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);

        var stockB = ProductStock.Start(HouseholdId.From(_household), kilosVariant, clock);
        clock.UtcNow = clock.UtcNow.AddDays(-7);
        stockB.AddStock(1m, kilos, _location, _user, clock);
        clock.UtcNow = clock.UtcNow.AddDays(7);
        stockB.Consume(0.1m, kilos, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        stockB.Consume(0.1m, kilos, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);

        var stocks = new FakeProductStockRepository();
        stocks.Items.AddRange([stockA, stockB]);
        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(parentId, "Parent", null, grams, "g", CanHoldStock: false));
        catalog.Products.Add(new CatalogProductInfo(gramsVariant, "Grams", null, grams, "g", CanHoldStock: true, ParentProductId: parentId));
        catalog.Products.Add(new CatalogProductInfo(kilosVariant, "Kilos", null, kilos, "kg", CanHoldStock: true, ParentProductId: parentId));
        var conversions = new PerProductConversionProvider(new Dictionary<Guid, IQuantityConverter>
        {
            [gramsVariant] = new IdentityQuantityConverter(),
            [kilosVariant] = new FactorQuantityConverter(new() { [(kilos, grams)] = 1000m }),
        });

        var stats = await ServiceWithConversions(stocks, catalog, conversions, clock)
            .GetConsumptionStatsAsync(parentId);

        Assert.NotNull(stats);
        Assert.Equal(120m, stats!.DaysOfSupply); // 880g ÷ (220g / 30d)
        Assert.Equal(0m, stats.WasteRate);
    }

    [Fact(DisplayName = "plantry-ikul: parent chips degrade independently on incomplete conversions")]
    public async Task GetConsumptionStats_Parent_SuppressesOnlyChipAffectedByConversionFailure()
    {
        var parentId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();
        var grams = Guid.CreateVersion7();
        var incompatible = Guid.CreateVersion7();
        var clock = new MutableClock();
        var stock = ProductStock.Start(HouseholdId.From(_household), variantId, clock);
        stock.AddStock(20m, grams, _location, _user, clock);
        stock.Consume(1m, grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        stock.Consume(1m, grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        // An incompatible active lot makes the on-hand total incomplete, but removal history still
        // converts fully, so waste remains available independently of days of supply.
        stock.AddStock(3m, incompatible, _location, _user, clock);
        var stocks = new FakeProductStockRepository();
        stocks.Items.Add(stock);
        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(parentId, "Parent", null, grams, "g", CanHoldStock: false));
        catalog.Products.Add(new CatalogProductInfo(variantId, "Variant", null, grams, "g", CanHoldStock: true, ParentProductId: parentId));
        var converter = new FactorQuantityConverter(new());

        var stats = await ServiceWithClock(stocks, catalog, converter, clock).GetConsumptionStatsAsync(parentId);

        Assert.NotNull(stats);
        Assert.Null(stats!.DaysOfSupply);
        Assert.Equal(0m, stats.WasteRate);
    }

    [Fact(DisplayName = "plantry-ikul: incompatible old consumption suppresses waste but not recent days")]
    public async Task GetConsumptionStats_Parent_OldConsumptionConversionFailurePreservesDays()
    {
        var parentId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();
        var grams = Guid.CreateVersion7();
        var incompatible = Guid.CreateVersion7();
        var clock = new MutableClock();
        var stock = ProductStock.Start(HouseholdId.From(_household), variantId, clock);
        clock.UtcNow = clock.UtcNow.AddDays(-200);
        stock.AddStock(1m, incompatible, _location, _user, clock);
        stock.Consume(1m, incompatible, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        clock.UtcNow = clock.UtcNow.AddDays(200);
        stock.AddStock(20m, grams, _location, _user, clock);
        stock.Consume(1m, grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        stock.Consume(1m, grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        var stocks = new FakeProductStockRepository();
        stocks.Items.Add(stock);
        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(parentId, "Parent", null, grams, "g", CanHoldStock: false));
        catalog.Products.Add(new CatalogProductInfo(variantId, "Variant", null, grams, "g", CanHoldStock: true, ParentProductId: parentId));
        var converter = new FactorQuantityConverter(new());

        var stats = await ServiceWithClock(stocks, catalog, converter, clock).GetConsumptionStatsAsync(parentId);

        Assert.NotNull(stats);
        Assert.Equal(810m, stats!.DaysOfSupply); // 18g ÷ (2g / 90d)
        Assert.Null(stats.WasteRate);
    }

    [Fact(DisplayName = "plantry-ikul: incompatible recent consumed rows suppress both parent chips")]
    public async Task GetConsumptionStats_Parent_RecentConsumptionConversionFailureSuppressesBothChips()
    {
        var parentId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();
        var grams = Guid.CreateVersion7();
        var incompatible = Guid.CreateVersion7();
        var clock = new MutableClock();
        var stock = ProductStock.Start(HouseholdId.From(_household), variantId, clock);
        stock.AddStock(10m, incompatible, _location, _user, clock);
        stock.Consume(1m, incompatible, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        stock.Consume(1m, incompatible, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        var stocks = new FakeProductStockRepository();
        stocks.Items.Add(stock);
        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(parentId, "Parent", null, grams, "g", CanHoldStock: false));
        catalog.Products.Add(new CatalogProductInfo(variantId, "Variant", null, grams, "g", CanHoldStock: true, ParentProductId: parentId));

        var stats = await ServiceWithClock(stocks, catalog, new FactorQuantityConverter(new()), clock)
            .GetConsumptionStatsAsync(parentId);

        Assert.Null(stats);
    }

    [Fact(DisplayName = "plantry-ikul: incompatible discarded rows suppress waste while preserving parent days")]
    public async Task GetConsumptionStats_Parent_DiscardConversionFailurePreservesDays()
    {
        var parentId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();
        var grams = Guid.CreateVersion7();
        var incompatible = Guid.CreateVersion7();
        var clock = new MutableClock();
        var stock = ProductStock.Start(HouseholdId.From(_household), variantId, clock);
        stock.AddStock(20m, grams, _location, _user, clock);
        stock.Consume(1m, grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        stock.Consume(1m, grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        var incompatibleLot = stock.AddStock(3m, incompatible, _location, _user, clock);
        stock.Consume(3m, incompatible, StockReason.Discarded, new IdentityQuantityConverter(), _user, clock,
            targetEntry: incompatibleLot.Id);
        var stocks = new FakeProductStockRepository();
        stocks.Items.Add(stock);
        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(parentId, "Parent", null, grams, "g", CanHoldStock: false));
        catalog.Products.Add(new CatalogProductInfo(variantId, "Variant", null, grams, "g", CanHoldStock: true, ParentProductId: parentId));

        var stats = await ServiceWithClock(stocks, catalog, new FactorQuantityConverter(new()), clock)
            .GetConsumptionStatsAsync(parentId);

        Assert.NotNull(stats);
        Assert.Equal(126m, stats!.DaysOfSupply); // 18g ÷ (2g / 14d)
        Assert.Null(stats.WasteRate);
    }

    [Fact(DisplayName = "plantry-fuej: returns null when the product has no stock record at all")]
    public async Task GetConsumptionStats_ReturnsNull_WhenNoStockRecord()
    {
        var stocks = new FakeProductStockRepository();
        var clock = new MutableClock();

        var stats = await ServiceWithClock(stocks, Catalog(), new IdentityQuantityConverter(), clock)
            .GetConsumptionStatsAsync(_productId);

        Assert.Null(stats);
    }

    [Fact(DisplayName = "plantry-hjk7: returns null for a stock record with no journal history")]
    public async Task GetConsumptionStats_ReturnsNull_WhenJournalIsEmpty()
    {
        var stocks = new FakeProductStockRepository();
        var clock = new MutableClock();
        stocks.Items.Add(ProductStock.Start(HouseholdId.From(_household), _productId, clock));

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

    [Fact(DisplayName = "plantry-fuej: mature history clamps velocity denominator at the trailing-window maximum")]
    public async Task GetConsumptionStats_DaysOfSupply_MatureHistory_UsesMaximumDenominator()
    {
        var stocks = new FakeProductStockRepository();
        var clock = new MutableClock();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, clock);
        clock.UtcNow = clock.UtcNow.AddDays(-100);
        stock.AddStock(1000m, _grams, _location, _user, clock);
        clock.UtcNow = clock.UtcNow.AddDays(100);
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

    [Fact(DisplayName = "plantry-hjk7: young history floors velocity denominator at minimum pace days")]
    public async Task GetConsumptionStats_DaysOfSupply_YoungHistory_UsesMinimumDenominator()
    {
        var stocks = new FakeProductStockRepository();
        var clock = new MutableClock();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, clock);
        clock.UtcNow = clock.UtcNow.AddDays(-7);
        stock.AddStock(1000m, _grams, _location, _user, clock);
        clock.UtcNow = clock.UtcNow.AddDays(7);
        stock.Consume(90m, _grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        stock.Consume(90m, _grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        stocks.Items.Add(stock);

        var stats = await ServiceWithClock(stocks, Catalog(), new IdentityQuantityConverter(), clock)
            .GetConsumptionStatsAsync(_productId);

        Assert.NotNull(stats);
        Assert.Equal(63.8m, stats!.DaysOfSupply);
    }

    [Fact(DisplayName = "plantry-hjk7: exact minimum observed span uses the unclamped denominator")]
    public async Task GetConsumptionStats_DaysOfSupply_ExactMinimumSpan_UsesObservedDenominator()
    {
        var stocks = new FakeProductStockRepository();
        var clock = new MutableClock();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, clock);
        clock.UtcNow = clock.UtcNow.AddDays(-14);
        stock.AddStock(1000m, _grams, _location, _user, clock);
        clock.UtcNow = clock.UtcNow.AddDays(14);
        stock.Consume(90m, _grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        stock.Consume(90m, _grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        stocks.Items.Add(stock);

        var stats = await ServiceWithClock(stocks, Catalog(), new IdentityQuantityConverter(), clock)
            .GetConsumptionStatsAsync(_productId);

        Assert.Equal(63.8m, stats!.DaysOfSupply);
    }

    [Fact(DisplayName = "plantry-hjk7: just above minimum observed span uses the 15-day denominator")]
    public async Task GetConsumptionStats_DaysOfSupply_JustAboveMinimumSpan_UsesObservedDenominator()
    {
        var stocks = new FakeProductStockRepository();
        var clock = new MutableClock();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, clock);
        clock.UtcNow = clock.UtcNow.AddDays(-15);
        stock.AddStock(1000m, _grams, _location, _user, clock);
        clock.UtcNow = clock.UtcNow.AddDays(15);
        stock.Consume(90m, _grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        stock.Consume(90m, _grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        stocks.Items.Add(stock);

        var stats = await ServiceWithClock(stocks, Catalog(), new IdentityQuantityConverter(), clock)
            .GetConsumptionStatsAsync(_productId);

        Assert.Equal(68.3m, stats!.DaysOfSupply);
    }

    [Fact(DisplayName = "plantry-hjk7: first Purchase journal entry controls the observed denominator")]
    public async Task GetConsumptionStats_DaysOfSupply_UsesFirstJournalEntry_NotFirstConsumption()
    {
        var stocks = new FakeProductStockRepository();
        var clock = new MutableClock();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, clock);
        clock.UtcNow = clock.UtcNow.AddDays(-30);
        stock.AddStock(1000m, _grams, _location, _user, clock);
        clock.UtcNow = clock.UtcNow.AddDays(30);
        stock.Consume(90m, _grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        stock.Consume(90m, _grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        stocks.Items.Add(stock);

        var stats = await ServiceWithClock(stocks, Catalog(), new IdentityQuantityConverter(), clock)
            .GetConsumptionStatsAsync(_productId);

        // The earlier Purchase journal entry makes the observed span 30 days, not zero.
        Assert.Equal(136.7m, stats!.DaysOfSupply);
    }

    [Fact(DisplayName = "plantry-hjk7: same-day first journal entry uses minimum pace denominator")]
    public async Task GetConsumptionStats_DaysOfSupply_SameDayHistory_UsesMinimumDenominator()
    {
        var stocks = new FakeProductStockRepository();
        var clock = new MutableClock();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, clock);
        stock.AddStock(1000m, _grams, _location, _user, clock);
        stock.Consume(90m, _grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        stock.Consume(90m, _grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        stocks.Items.Add(stock);

        var stats = await ServiceWithClock(stocks, Catalog(), new IdentityQuantityConverter(), clock)
            .GetConsumptionStatsAsync(_productId);

        Assert.Equal(63.8m, stats!.DaysOfSupply);
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
        stock.Consume(20m, _grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, clock);
        stocks.Items.Add(stock);

        var stats = await ServiceWithClock(stocks, Catalog(), new IdentityQuantityConverter(), clock)
            .GetConsumptionStatsAsync(_productId);

        // The two recent rows total 30g; the excluded 500g row must not affect the 90-day numerator.
        // The first journal entry is 200 days old, so the denominator clamps at 90 days;
        // on hand is 470g and 470 ÷ (30 ÷ 90) = 1410 days.
        Assert.Equal(1410m, stats!.DaysOfSupply);
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
        // On hand: 1000g - 180g = 820g, at 180g / 14-day minimum pace = 63.8 days,
        // proving mixed-unit journal rows converted correctly under the clamped denominator.
        Assert.Equal(63.8m, stats!.DaysOfSupply);
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

    private sealed class PerProductConversionProvider(
        IReadOnlyDictionary<Guid, IQuantityConverter> converters) : IProductConversionProvider
    {
        public Task<IQuantityConverter> ForProductAsync(Guid productId, CancellationToken ct = default) =>
            Task.FromResult(converters.TryGetValue(productId, out var converter)
                ? converter
                : (IQuantityConverter)new IdentityQuantityConverter());

        public Task<IReadOnlyDictionary<Guid, IQuantityConverter>> ForProductsAsync(
            IEnumerable<Guid> productIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, IQuantityConverter>>(productIds.Distinct()
                .ToDictionary(id => id, id => converters.TryGetValue(id, out var converter)
                    ? converter
                    : (IQuantityConverter)new IdentityQuantityConverter()));
    }
}
