using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Unit.Inventory.Application;
using Plantry.Web.Recipes;

namespace Plantry.Tests.Unit.Composition;

/// <summary>
/// L2 tests for <see cref="InventoryStockReaderAdapter"/> (plantry-riqy) — the Recipes→Inventory ACL
/// adapter <c>FulfillmentService</c> uses for live stock snapshots. Covers the single-product path
/// (delegates to the batch path), the batch aggregation (FEFO-summed quantity in the default unit,
/// soonest expiry), the no-household / empty-input short-circuits, and the depleted/uncatalogued skips.
/// </summary>
public sealed class InventoryStockReaderAdapterTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly Guid LocationId = Guid.CreateVersion7();
    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero));

    private static InventoryStockReaderAdapter Adapter(
        FakeProductStockRepository stocks, FakeCatalogReadFacade catalog, Guid? household) =>
        new(stocks, catalog, new FakeConversionProvider(new IdentityQuantityConverter()), Clock, new FakeTenantContext(household));

    [Fact(DisplayName = "FindStockAsync (single) delegates to the batch path and returns the product's snapshot")]
    public async Task FindStockAsync_Delegates_To_Batch()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.CreateVersion7();
        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(productId, "Milk", null, unitId, "ea", CanHoldStock: true));
        var stock = ProductStock.Start(Household, productId, Clock);
        stock.AddStock(4m, unitId, LocationId, UserId, Clock);
        var stocks = new FakeProductStockRepository();
        stocks.Items.Add(stock);

        var result = await Adapter(stocks, catalog, Household.Value).FindStockAsync(productId);

        Assert.NotNull(result);
        Assert.Equal(4m, result!.AvailableQuantity);
        Assert.Equal(unitId, result.DefaultUnitId);
    }

    [Fact(DisplayName = "FindStockAsync (single) returns null for a product with no available stock")]
    public async Task FindStockAsync_Returns_Null_For_No_Stock()
    {
        var result = await Adapter(new FakeProductStockRepository(), new FakeCatalogReadFacade(), Household.Value)
            .FindStockAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact(DisplayName = "FindStockBatchAsync short-circuits on an empty product id list")]
    public async Task FindStockBatchAsync_ShortCircuits_On_Empty_Input()
    {
        var result = await Adapter(new FakeProductStockRepository(), new FakeCatalogReadFacade(), Household.Value)
            .FindStockBatchAsync([]);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "FindStockBatchAsync returns empty when the tenant carries no household")]
    public async Task FindStockBatchAsync_Returns_Empty_When_No_Household()
    {
        var result = await Adapter(new FakeProductStockRepository(), new FakeCatalogReadFacade(), household: null)
            .FindStockBatchAsync([Guid.NewGuid()]);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "FindStockBatchAsync aggregates active lots per product into the default unit with soonest expiry")]
    public async Task FindStockBatchAsync_Aggregates_Active_Lots()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.CreateVersion7();
        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(productId, "Milk", null, unitId, "ea", CanHoldStock: true));

        var stock = ProductStock.Start(Household, productId, Clock);
        stock.AddStock(2m, unitId, LocationId, UserId, Clock, expiryDate: new DateOnly(2026, 7, 25));
        stock.AddStock(3m, unitId, LocationId, UserId, Clock, expiryDate: new DateOnly(2026, 7, 20));
        var stocks = new FakeProductStockRepository();
        stocks.Items.Add(stock);

        // Decoy: a second catalogued product with live stock that the call does NOT ask for (it must be
        // catalogued, or it would be dropped by the catalog miss instead of by the wanted-set filter), so
        // Assert.Single can only hold if the adapter filters ListForHouseholdAsync to the requested ids.
        var decoyId = Guid.NewGuid();
        catalog.Products.Add(new CatalogProductInfo(decoyId, "Bread", null, unitId, "ea", CanHoldStock: true));
        var decoyStock = ProductStock.Start(Household, decoyId, Clock);
        decoyStock.AddStock(1m, unitId, LocationId, UserId, Clock);
        stocks.Items.Add(decoyStock);

        var result = await Adapter(stocks, catalog, Household.Value).FindStockBatchAsync([productId]);

        Assert.Single(result);
        var snapshot = result[productId];
        Assert.Equal(5m, snapshot.AvailableQuantity);
        Assert.Equal(new DateOnly(2026, 7, 20), snapshot.SoonestExpiry);
    }

    [Fact(DisplayName = "FindStockBatchAsync omits a product whose lots are all depleted")]
    public async Task FindStockBatchAsync_Omits_Depleted_Product()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.CreateVersion7();
        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(productId, "Milk", null, unitId, "ea", CanHoldStock: true));
        var stock = ProductStock.Start(Household, productId, Clock);
        stock.AddStock(4m, unitId, LocationId, UserId, Clock);
        stock.Consume(4m, unitId, StockReason.Consumed, new IdentityQuantityConverter(), UserId, Clock);
        var stocks = new FakeProductStockRepository();
        stocks.Items.Add(stock);

        var result = await Adapter(stocks, catalog, Household.Value).FindStockBatchAsync([productId]);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "FindStockBatchAsync omits a product no longer present in Catalog")]
    public async Task FindStockBatchAsync_Omits_Uncatalogued_Product()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.CreateVersion7();
        var stock = ProductStock.Start(Household, productId, Clock);
        stock.AddStock(1m, unitId, LocationId, UserId, Clock);
        var stocks = new FakeProductStockRepository();
        stocks.Items.Add(stock);

        var result = await Adapter(stocks, new FakeCatalogReadFacade(), Household.Value).FindStockBatchAsync([productId]);

        Assert.Empty(result);
    }
}
