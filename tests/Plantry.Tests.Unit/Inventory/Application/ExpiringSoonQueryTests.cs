using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Inventory.Application;

/// <summary>
/// L2 unit tests for <see cref="InventoryQueryService.ExpiringSoonAsync"/>.
///
/// Covers:
/// <list type="bullet">
///   <item>Window boundary — items exactly at 7 days appear; items beyond 7 days do not.</item>
///   <item>Expired vs soon distinction — past expiry date yields IsExpired=true, DaysLeft=0.</item>
///   <item>Ordering — soonest-first (expired lots first by date, then ascending).</item>
///   <item>Top-N cap — at most ExpiringSoonMaxItems results returned.</item>
///   <item>Tenancy — no household in context returns empty.</item>
///   <item>Products with no dated lots are excluded.</item>
///   <item>Empty-lot products (fully depleted) are excluded.</item>
/// </list>
/// </summary>
public sealed class ExpiringSoonQueryTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _product1 = Guid.CreateVersion7();
    private readonly Guid _product2 = Guid.CreateVersion7();
    private readonly Guid _product3 = Guid.CreateVersion7();
    private readonly Guid _grams = Guid.CreateVersion7();
    private readonly Guid _location = Guid.CreateVersion7();
    private readonly Guid _user = Guid.CreateVersion7();

    private InventoryQueryService Service(
        FakeProductStockRepository stocks, FakeCatalogReadFacade catalog,
        IQuantityConverter converter, Guid? household,
        int horizonDays = HouseholdInventorySettings.DefaultExpiringSoonDays) =>
        new(stocks, catalog, new FakeConversionProvider(converter),
            new FakeExpiringSoonHorizon(horizonDays), Clock, new FakeTenantContext(household));

    private FakeCatalogReadFacade Catalog(params (Guid id, string name)[] products)
    {
        var catalog = new FakeCatalogReadFacade();
        foreach (var (id, name) in products)
            catalog.Products.Add(new CatalogProductInfo(id, name, "Pantry", _grams, "g", CanHoldStock: true));
        catalog.UnitCodes[_grams] = "g";
        catalog.LocationNames[_location] = "Pantry";
        return catalog;
    }

    // ── Window boundary ──────────────────────────────────────────────────────

    [Fact(DisplayName = "ExpiringSoon — item expiring exactly at the 7-day boundary is included")]
    public async Task ExpiringSoon_AtWindowBoundary_IsIncluded()
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _product1, Clock);
        stock.AddStock(100m, _grams, _location, _user, Clock, expiryDate: Today.AddDays(7));
        stocks.Items.Add(stock);

        var result = await Service(stocks, Catalog((_product1, "Milk")), new IdentityQuantityConverter(), _household)
            .ExpiringSoonAsync();

        var item = Assert.Single(result);
        Assert.Equal("Milk", item.Name);
        Assert.Equal(7, item.DaysLeft);
        Assert.False(item.IsExpired);
    }

    [Fact(DisplayName = "ExpiringSoon — item expiring beyond the 7-day boundary is excluded")]
    public async Task ExpiringSoon_BeyondWindowBoundary_IsExcluded()
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _product1, Clock);
        stock.AddStock(100m, _grams, _location, _user, Clock, expiryDate: Today.AddDays(8));
        stocks.Items.Add(stock);

        var result = await Service(stocks, Catalog((_product1, "Flour")), new IdentityQuantityConverter(), _household)
            .ExpiringSoonAsync();

        Assert.Empty(result);
    }

    [Fact(DisplayName = "ExpiringSoon — the window follows the configured horizon, not the default")]
    public async Task ExpiringSoon_Honors_Configured_Horizon()
    {
        // Configured horizon of 3: a lot 3 days out is in the window, one 4 days out is not.
        // Under the default (7) both would appear — this passes only if the configured value drives it.
        var stocks = new FakeProductStockRepository();
        var within = ProductStock.Start(HouseholdId.From(_household), _product1, Clock);
        within.AddStock(100m, _grams, _location, _user, Clock, expiryDate: Today.AddDays(3));
        var beyond = ProductStock.Start(HouseholdId.From(_household), _product2, Clock);
        beyond.AddStock(100m, _grams, _location, _user, Clock, expiryDate: Today.AddDays(4));
        stocks.Items.Add(within);
        stocks.Items.Add(beyond);

        var result = await Service(
                stocks, Catalog((_product1, "Milk"), (_product2, "Flour")),
                new IdentityQuantityConverter(), _household, horizonDays: 3)
            .ExpiringSoonAsync();

        var item = Assert.Single(result);
        Assert.Equal(_product1, item.ProductId);
    }

    [Fact(DisplayName = "ExpiringSoon — product with no dated lots is excluded")]
    public async Task ExpiringSoon_NoDatedLots_IsExcluded()
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _product1, Clock);
        stock.AddStock(100m, _grams, _location, _user, Clock, expiryDate: null);
        stocks.Items.Add(stock);

        var result = await Service(stocks, Catalog((_product1, "Salt")), new IdentityQuantityConverter(), _household)
            .ExpiringSoonAsync();

        Assert.Empty(result);
    }

    // ── Expired vs soon distinction ─────────────────────────────────────────

    [Fact(DisplayName = "ExpiringSoon — lot already past today is returned with IsExpired=true, DaysLeft=0")]
    public async Task ExpiringSoon_PastExpiry_IsExpiredTrueAndDaysLeftZero()
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _product1, Clock);
        stock.AddStock(50m, _grams, _location, _user, Clock, expiryDate: Today.AddDays(-3));
        stocks.Items.Add(stock);

        var result = await Service(stocks, Catalog((_product1, "Yogurt")), new IdentityQuantityConverter(), _household)
            .ExpiringSoonAsync();

        var item = Assert.Single(result);
        Assert.True(item.IsExpired);
        Assert.Equal(0, item.DaysLeft);
        Assert.Equal(Today.AddDays(-3), item.SoonestExpiry);
    }

    [Fact(DisplayName = "ExpiringSoon — lot expiring today has DaysLeft=0, IsExpired=false")]
    public async Task ExpiringSoon_ExpiringToday_DaysLeftZeroIsExpiredFalse()
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _product1, Clock);
        stock.AddStock(50m, _grams, _location, _user, Clock, expiryDate: Today);
        stocks.Items.Add(stock);

        var result = await Service(stocks, Catalog((_product1, "Cheese")), new IdentityQuantityConverter(), _household)
            .ExpiringSoonAsync();

        var item = Assert.Single(result);
        Assert.False(item.IsExpired);
        Assert.Equal(0, item.DaysLeft);
    }

    [Fact(DisplayName = "ExpiringSoon — lot expiring tomorrow has DaysLeft=1")]
    public async Task ExpiringSoon_ExpiringTomorrow_DaysLeftOne()
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _product1, Clock);
        stock.AddStock(50m, _grams, _location, _user, Clock, expiryDate: Today.AddDays(1));
        stocks.Items.Add(stock);

        var result = await Service(stocks, Catalog((_product1, "Eggs")), new IdentityQuantityConverter(), _household)
            .ExpiringSoonAsync();

        var item = Assert.Single(result);
        Assert.False(item.IsExpired);
        Assert.Equal(1, item.DaysLeft);
    }

    // ── Ordering ─────────────────────────────────────────────────────────────

    [Fact(DisplayName = "ExpiringSoon — results are ordered soonest expiry first (expired before future)")]
    public async Task ExpiringSoon_OrderedSoonestFirst()
    {
        var stocks = new FakeProductStockRepository();

        var s1 = ProductStock.Start(HouseholdId.From(_household), _product1, Clock);
        s1.AddStock(100m, _grams, _location, _user, Clock, expiryDate: Today.AddDays(5));
        stocks.Items.Add(s1);

        var s2 = ProductStock.Start(HouseholdId.From(_household), _product2, Clock);
        s2.AddStock(100m, _grams, _location, _user, Clock, expiryDate: Today.AddDays(-1)); // expired
        stocks.Items.Add(s2);

        var s3 = ProductStock.Start(HouseholdId.From(_household), _product3, Clock);
        s3.AddStock(100m, _grams, _location, _user, Clock, expiryDate: Today.AddDays(2));
        stocks.Items.Add(s3);

        var catalog = Catalog((_product1, "Apple"), (_product2, "Banana"), (_product3, "Carrot"));
        var result = await Service(stocks, catalog, new IdentityQuantityConverter(), _household)
            .ExpiringSoonAsync();

        Assert.Equal(3, result.Count);
        // Expired first, then by ascending date
        Assert.Equal("Banana", result[0].Name); // -1 days (expired)
        Assert.Equal("Carrot", result[1].Name); // +2 days
        Assert.Equal("Apple", result[2].Name);  // +5 days
    }

    // ── Top-N cap ────────────────────────────────────────────────────────────

    [Fact(DisplayName = "ExpiringSoon — returns at most ExpiringSoonMaxItems results")]
    public async Task ExpiringSoon_ExceedsMaxItems_Capped()
    {
        var stocks = new FakeProductStockRepository();
        var catalog = new FakeCatalogReadFacade();
        catalog.UnitCodes[_grams] = "g";

        // Seed ExpiringSoonMaxItems + 2 products, all expiring today or yesterday (within/past window)
        // so all qualify — the cap must trim to ExpiringSoonMaxItems.
        var totalCount = InventoryQueryService.ExpiringSoonMaxItems + 2;
        var productIds = Enumerable.Range(0, totalCount)
            .Select(_ => Guid.CreateVersion7())
            .ToList();

        foreach (var (id, i) in productIds.Select((id, i) => (id, i)))
        {
            catalog.Products.Add(new CatalogProductInfo(id, $"Product {i:D2}", "Cat", _grams, "g", CanHoldStock: true));
            var stock = ProductStock.Start(HouseholdId.From(_household), id, Clock);
            // Use days 0..totalCount-1 modulo window so all items are within the expiry window.
            // Expired items (i >= 1 → negative offset) and today (i=0) are all in scope.
            stock.AddStock(100m, _grams, _location, _user, Clock,
                expiryDate: Today.AddDays(-(i % (HouseholdInventorySettings.DefaultExpiringSoonDays + 1))));
            stocks.Items.Add(stock);
        }

        var result = await Service(stocks, catalog, new IdentityQuantityConverter(), _household)
            .ExpiringSoonAsync();

        Assert.Equal(InventoryQueryService.ExpiringSoonMaxItems, result.Count);
    }

    // ── Tenancy ──────────────────────────────────────────────────────────────

    [Fact(DisplayName = "ExpiringSoon — returns empty when no household in context")]
    public async Task ExpiringSoon_NoHousehold_ReturnsEmpty()
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _product1, Clock);
        stock.AddStock(100m, _grams, _location, _user, Clock, expiryDate: Today.AddDays(1));
        stocks.Items.Add(stock);

        var result = await Service(stocks, Catalog((_product1, "Milk")), new IdentityQuantityConverter(), household: null)
            .ExpiringSoonAsync();

        Assert.Empty(result);
    }

    [Fact(DisplayName = "ExpiringSoon — only returns items for the active household (tenancy isolation)")]
    public async Task ExpiringSoon_OtherHousehold_NotReturned()
    {
        var otherHousehold = Guid.NewGuid();

        var stocks = new FakeProductStockRepository();

        // Stock for our household — expiring soon
        var ownStock = ProductStock.Start(HouseholdId.From(_household), _product1, Clock);
        ownStock.AddStock(100m, _grams, _location, _user, Clock, expiryDate: Today.AddDays(2));
        stocks.Items.Add(ownStock);

        // Stock for another household — also expiring soon; must not appear
        var otherStock = ProductStock.Start(HouseholdId.From(otherHousehold), _product2, Clock);
        otherStock.AddStock(100m, _grams, _location, _user, Clock, expiryDate: Today.AddDays(1));
        stocks.Items.Add(otherStock);

        var catalog = Catalog((_product1, "Milk"), (_product2, "Juice"));
        var result = await Service(stocks, catalog, new IdentityQuantityConverter(), _household)
            .ExpiringSoonAsync();

        Assert.Single(result);
        Assert.Equal("Milk", result[0].Name);
    }

    // ── Misc ──────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "ExpiringSoon — fully depleted product is excluded")]
    public async Task ExpiringSoon_DepletedProduct_IsExcluded()
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _product1, Clock);
        stock.AddStock(10m, _grams, _location, _user, Clock, expiryDate: Today.AddDays(1));
        stock.Consume(10m, _grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, Clock);
        stocks.Items.Add(stock);

        var result = await Service(stocks, Catalog((_product1, "Milk")), new IdentityQuantityConverter(), _household)
            .ExpiringSoonAsync();

        Assert.Empty(result);
    }

    [Fact(DisplayName = "ExpiringSoon — item carries correct quantity, unit, and location")]
    public async Task ExpiringSoon_PopulatesQuantityUnitAndLocation()
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _product1, Clock);
        stock.AddStock(250m, _grams, _location, _user, Clock, expiryDate: Today.AddDays(3));
        stocks.Items.Add(stock);

        var result = await Service(stocks, Catalog((_product1, "Butter")), new IdentityQuantityConverter(), _household)
            .ExpiringSoonAsync();

        var item = Assert.Single(result);
        Assert.Equal("Butter", item.Name);
        Assert.Equal(250m, item.TotalQuantity);
        Assert.Equal("g", item.DisplayUnitCode);
        Assert.Equal("Pantry", item.LocationDisplay);
        Assert.Equal(Today.AddDays(3), item.SoonestExpiry);
        Assert.Equal(3, item.DaysLeft);
        Assert.False(item.IsExpired);
    }
}
