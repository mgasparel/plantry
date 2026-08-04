using Plantry.Pantry.Application;
using Plantry.Pantry.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Unit.Inventory.Application;
using Plantry.Web.MealPlanning;

namespace Plantry.Tests.Unit.Composition;

/// <summary>
/// L2 tests for <see cref="MealPlanStockReaderAdapter"/> (plantry-riqy) — the MealPlanning→Inventory ACL
/// adapter <c>ShopForWeekService</c> uses to resolve a product's on-hand quantity. Mirrors the Recipes
/// <c>InventoryStockReaderAdapter</c> single-product path: unknown product, never-stocked zero-qty
/// snapshot (so a shopping-list add is never silently dropped), and the FEFO-aggregated happy path.
/// </summary>
public sealed class MealPlanStockReaderAdapterTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly Guid LocationId = Guid.CreateVersion7();

    private static MealPlanStockReaderAdapter Adapter(
        FakeProductStockRepository stocks, FakeCatalogReadFacade catalog, Guid? household) =>
        new(stocks, catalog, new FakeConversionProvider(new IdentityQuantityConverter()), new FakeTenantContext(household));

    [Fact(DisplayName = "FindStockAsync returns null for a product unknown to Catalog")]
    public async Task Returns_Null_For_Unknown_Product()
    {
        var result = await Adapter(new FakeProductStockRepository(), new FakeCatalogReadFacade(), Household.Value)
            .FindStockAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact(DisplayName = "FindStockAsync returns a zero-quantity snapshot for a never-stocked but catalogued product")]
    public async Task Returns_Zero_Quantity_Snapshot_For_Never_Stocked_Product()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.CreateVersion7();
        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(productId, "Milk", null, unitId, "ea", CanHoldStock: true));

        var result = await Adapter(new FakeProductStockRepository(), catalog, Household.Value).FindStockAsync(productId);

        Assert.NotNull(result);
        Assert.Equal(0m, result!.AvailableQuantity);
        Assert.Equal(unitId, result.DefaultUnitId);
        Assert.Null(result.SoonestExpiry);
    }

    [Fact(DisplayName = "FindStockAsync aggregates active lots into the product's default unit with the soonest expiry")]
    public async Task Aggregates_Active_Lots()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.CreateVersion7();
        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(productId, "Milk", null, unitId, "ea", CanHoldStock: true));

        var stocks = new FakeProductStockRepository();
        // Decoy FIRST (the fake preserves insertion order): a different product's live stock, so
        // FirstOrDefault's ProductId predicate is load-bearing — without it the adapter would aggregate
        // this 99-unit lot and report 2026-07-01 as the soonest expiry.
        var decoy = ProductStock.Start(Household, Guid.NewGuid(), SystemClock.Instance);
        decoy.AddStock(99m, unitId, LocationId, UserId, SystemClock.Instance, expiryDate: new DateOnly(2026, 7, 1));
        stocks.Items.Add(decoy);

        var stock = ProductStock.Start(Household, productId, SystemClock.Instance);
        stock.AddStock(2m, unitId, LocationId, UserId, SystemClock.Instance, expiryDate: new DateOnly(2026, 7, 25));
        stock.AddStock(3m, unitId, LocationId, UserId, SystemClock.Instance, expiryDate: new DateOnly(2026, 7, 20));
        stocks.Items.Add(stock);

        var result = await Adapter(stocks, catalog, Household.Value).FindStockAsync(productId);

        Assert.NotNull(result);
        Assert.Equal(5m, result!.AvailableQuantity);
        Assert.Equal(new DateOnly(2026, 7, 20), result.SoonestExpiry);
    }

    [Fact(DisplayName = "FindStockAsync returns null when the tenant carries no household")]
    public async Task Returns_Null_When_No_Household()
    {
        var productId = Guid.NewGuid();
        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(productId, "Milk", null, Guid.CreateVersion7(), "ea", CanHoldStock: true));

        var result = await Adapter(new FakeProductStockRepository(), catalog, household: null).FindStockAsync(productId);

        Assert.Null(result);
    }
}
