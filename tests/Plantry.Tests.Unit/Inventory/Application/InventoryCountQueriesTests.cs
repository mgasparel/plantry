using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Inventory.Application;

/// <summary>
/// L2 unit tests for the lean count queries <see cref="InventoryQueryService.CountInStockAsync"/> and
/// <see cref="InventoryQueryService.CountExpiringSoonAsync"/>.
///
/// The load-bearing invariant is agreement with the list queries they summarise:
/// <list type="bullet">
///   <item>CountInStock equals the row count of ListPantryAsync (same inclusion predicate) across
///         multi-lot, zero-stock (depleted), and orphan-product cases.</item>
///   <item>CountExpiringSoon equals ExpiringSoonAsync's count while under the cap, and exceeds it when
///         more than ExpiringSoonMaxItems qualify (it is uncapped).</item>
///   <item>Expired lots count; undated lots don't; lots beyond the horizon don't.</item>
///   <item>Tenancy — no household in context returns 0.</item>
/// </list>
/// </summary>
public sealed class InventoryCountQueriesTests
{
    // Pinned instant (mid-day, mid-month) so expiry-window fixtures can never straddle a UTC-day
    // boundary between seeding and the query. Matches the SUT's Today() = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).
    private static readonly DateTimeOffset Now = new(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly IClock Clock = new FixedClock(Now);
    private static DateOnly Today => DateOnly.FromDateTime(Now.UtcDateTime);

    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _grams = Guid.CreateVersion7();
    private readonly Guid _location = Guid.CreateVersion7();
    private readonly Guid _user = Guid.CreateVersion7();

    private InventoryQueryService Service(
        FakeProductStockRepository stocks, FakeCatalogReadFacade catalog, Guid? household,
        int horizonDays = HouseholdInventorySettings.DefaultExpiringSoonDays) =>
        new(stocks, catalog, new FakeConversionProvider(new IdentityQuantityConverter()),
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

    private ProductStock StockWith(Guid productId, params (decimal qty, DateOnly? expiry)[] lots)
    {
        var stock = ProductStock.Start(HouseholdId.From(_household), productId, Clock);
        foreach (var (qty, expiry) in lots)
            stock.AddStock(qty, _grams, _location, _user, Clock, expiryDate: expiry);
        return stock;
    }

    // ── CountInStockAsync ────────────────────────────────────────────────────

    [Fact(DisplayName = "CountInStock — agrees with ListPantry row count across multi-lot, depleted, and orphan cases")]
    public async Task CountInStock_Agrees_With_ListPantry_RowCount()
    {
        var multiLot = Guid.CreateVersion7();   // 2 active lots → one pantry row
        var depleted = Guid.CreateVersion7();    // fully consumed → not a row
        var dated = Guid.CreateVersion7();       // single dated lot → one row
        var orphan = Guid.CreateVersion7();      // has stock but missing from catalog → not a row

        var stocks = new FakeProductStockRepository();
        stocks.Items.Add(StockWith(multiLot, (500m, null), (250m, Today.AddDays(30))));

        var depletedStock = StockWith(depleted, (10m, null));
        depletedStock.Consume(10m, _grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, Clock);
        stocks.Items.Add(depletedStock);

        stocks.Items.Add(StockWith(dated, (100m, Today.AddDays(2))));
        stocks.Items.Add(StockWith(orphan, (100m, null)));

        // orphan intentionally omitted from the catalog
        var catalog = Catalog((multiLot, "Flour"), (depleted, "Sugar"), (dated, "Milk"));
        var service = Service(stocks, catalog, _household);

        var pantry = await service.ListPantryAsync();
        var count = await service.CountInStockAsync();

        Assert.Equal(2, pantry.Count);        // multiLot + dated
        Assert.Equal(pantry.Count, count);
    }

    [Fact(DisplayName = "CountInStock — empty pantry (no stock) is 0, agreeing with ListPantry")]
    public async Task CountInStock_EmptyPantry_IsZero()
    {
        var stocks = new FakeProductStockRepository();
        var service = Service(stocks, Catalog(), _household);

        Assert.Empty(await service.ListPantryAsync());
        Assert.Equal(0, await service.CountInStockAsync());
    }

    [Fact(DisplayName = "CountInStock — returns 0 when no household in context")]
    public async Task CountInStock_NoHousehold_ReturnsZero()
    {
        var product = Guid.CreateVersion7();
        var stocks = new FakeProductStockRepository();
        stocks.Items.Add(StockWith(product, (100m, null)));

        var count = await Service(stocks, Catalog((product, "Milk")), household: null).CountInStockAsync();

        Assert.Equal(0, count);
    }

    [Fact(DisplayName = "CountInStock — counts only the active household's stock (tenancy isolation)")]
    public async Task CountInStock_OtherHousehold_NotCounted()
    {
        var mine = Guid.CreateVersion7();
        var theirs = Guid.CreateVersion7();

        var stocks = new FakeProductStockRepository();
        stocks.Items.Add(StockWith(mine, (100m, null)));

        var otherStock = ProductStock.Start(HouseholdId.From(Guid.NewGuid()), theirs, Clock);
        otherStock.AddStock(100m, _grams, _location, _user, Clock);
        stocks.Items.Add(otherStock);

        var count = await Service(stocks, Catalog((mine, "Milk"), (theirs, "Juice")), _household).CountInStockAsync();

        Assert.Equal(1, count);
    }

    // ── CountExpiringSoonAsync ───────────────────────────────────────────────

    [Fact(DisplayName = "CountExpiringSoon — under the cap, equals ExpiringSoon's count")]
    public async Task CountExpiringSoon_UnderCap_MatchesExpiringSoon()
    {
        var soon = Guid.CreateVersion7();      // within window → counts
        var expired = Guid.CreateVersion7();   // past → counts
        var beyond = Guid.CreateVersion7();    // beyond horizon → excluded
        var undated = Guid.CreateVersion7();   // no dated lot → excluded

        var stocks = new FakeProductStockRepository();
        stocks.Items.Add(StockWith(soon, (100m, Today.AddDays(2))));
        stocks.Items.Add(StockWith(expired, (100m, Today.AddDays(-3))));
        stocks.Items.Add(StockWith(beyond, (100m, Today.AddDays(HouseholdInventorySettings.DefaultExpiringSoonDays + 5))));
        stocks.Items.Add(StockWith(undated, (100m, null)));

        var catalog = Catalog((soon, "Milk"), (expired, "Yogurt"), (beyond, "Flour"), (undated, "Salt"));
        var service = Service(stocks, catalog, _household);

        var list = await service.ExpiringSoonAsync();
        var count = await service.CountExpiringSoonAsync();

        Assert.Equal(2, list.Count);       // soon + expired
        Assert.Equal(list.Count, count);
    }

    [Fact(DisplayName = "CountExpiringSoon — expired lots count; undated lots don't")]
    public async Task CountExpiringSoon_Counts_Expired_Excludes_Undated()
    {
        var expired = Guid.CreateVersion7();
        var undated = Guid.CreateVersion7();

        var stocks = new FakeProductStockRepository();
        stocks.Items.Add(StockWith(expired, (100m, Today.AddDays(-10))));
        stocks.Items.Add(StockWith(undated, (100m, null)));

        var count = await Service(stocks, Catalog((expired, "Yogurt"), (undated, "Salt")), _household)
            .CountExpiringSoonAsync();

        Assert.Equal(1, count);
    }

    [Fact(DisplayName = "CountExpiringSoon — is uncapped: exceeds ExpiringSoonMaxItems when more qualify")]
    public async Task CountExpiringSoon_ExceedsCap_WhenMoreThanMaxItemsQualify()
    {
        var total = InventoryQueryService.ExpiringSoonMaxItems + 2;
        var stocks = new FakeProductStockRepository();
        var catalog = new FakeCatalogReadFacade();
        catalog.UnitCodes[_grams] = "g";
        catalog.LocationNames[_location] = "Pantry";

        foreach (var i in Enumerable.Range(0, total))
        {
            var id = Guid.CreateVersion7();
            catalog.Products.Add(new CatalogProductInfo(id, $"Product {i:D2}", "Cat", _grams, "g", CanHoldStock: true));
            // All within/at the window (today or a few days out) so every one qualifies.
            stocks.Items.Add(StockWith(id, (100m, Today.AddDays(i % (HouseholdInventorySettings.DefaultExpiringSoonDays + 1)))));
        }

        var service = Service(stocks, catalog, _household);

        var list = await service.ExpiringSoonAsync();
        var count = await service.CountExpiringSoonAsync();

        Assert.Equal(InventoryQueryService.ExpiringSoonMaxItems, list.Count); // list is capped
        Assert.Equal(total, count);                                          // count is not
    }

    [Fact(DisplayName = "CountExpiringSoon — the window follows the configured horizon, not the default")]
    public async Task CountExpiringSoon_Honors_Configured_Horizon()
    {
        var within = Guid.CreateVersion7();
        var beyond = Guid.CreateVersion7();

        var stocks = new FakeProductStockRepository();
        stocks.Items.Add(StockWith(within, (100m, Today.AddDays(3))));
        stocks.Items.Add(StockWith(beyond, (100m, Today.AddDays(4))));

        var count = await Service(stocks, Catalog((within, "Milk"), (beyond, "Flour")), _household, horizonDays: 3)
            .CountExpiringSoonAsync();

        Assert.Equal(1, count);
    }

    [Fact(DisplayName = "CountExpiringSoon — fully depleted product with a soon date is excluded")]
    public async Task CountExpiringSoon_DepletedProduct_Excluded()
    {
        var product = Guid.CreateVersion7();
        var stocks = new FakeProductStockRepository();
        var stock = StockWith(product, (10m, Today.AddDays(1)));
        stock.Consume(10m, _grams, StockReason.Consumed, new IdentityQuantityConverter(), _user, Clock);
        stocks.Items.Add(stock);

        var count = await Service(stocks, Catalog((product, "Milk")), _household).CountExpiringSoonAsync();

        Assert.Equal(0, count);
    }

    [Fact(DisplayName = "CountExpiringSoon — returns 0 when no household in context")]
    public async Task CountExpiringSoon_NoHousehold_ReturnsZero()
    {
        var product = Guid.CreateVersion7();
        var stocks = new FakeProductStockRepository();
        stocks.Items.Add(StockWith(product, (100m, Today.AddDays(1))));

        var count = await Service(stocks, Catalog((product, "Milk")), household: null).CountExpiringSoonAsync();

        Assert.Equal(0, count);
    }
}
