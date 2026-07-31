using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Unit.Inventory.Application;
using Plantry.Web.MealPlanning;

namespace Plantry.Tests.Unit.Composition;

/// <summary>
/// L2 tests for <see cref="MealPlanExpiringStockReaderAdapter"/> (plantry-riqy, P3-5) — the
/// MealPlanning→Inventory ACL adapter that finds products with stock expiring within a window. Covers
/// both window edges (expiring today and expiring exactly at the cutoff), the just-past-cutoff and
/// already-expired omissions, the no-expiry-date skip, and the no-household degrade.
/// </summary>
public sealed class MealPlanExpiringStockReaderAdapterTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly Guid UnitId = Guid.CreateVersion7();
    private static readonly Guid LocationId = Guid.CreateVersion7();
    private static readonly DateOnly Today = new(2026, 7, 18);

    private static ProductStock StockWithExpiry(Guid productId, DateOnly? expiryDate)
    {
        var stock = ProductStock.Start(Household, productId, SystemClock.Instance);
        stock.AddStock(1m, UnitId, LocationId, UserId, SystemClock.Instance, expiryDate: expiryDate);
        return stock;
    }

    [Fact(DisplayName = "GetExpiringProductIdsAsync returns a product whose soonest lot expires exactly at the cutoff")]
    public async Task Returns_Product_Expiring_Within_Window()
    {
        var productId = Guid.NewGuid();
        var stocks = new FakeProductStockRepository();
        stocks.Items.Add(StockWithExpiry(productId, Today.AddDays(5)));
        var adapter = new MealPlanExpiringStockReaderAdapter(stocks, new FakeTenantContext(Household.Value));

        var result = await adapter.GetExpiringProductIdsAsync(Today, withinDays: 5);

        Assert.Contains(productId, result);
    }

    [Fact(DisplayName = "GetExpiringProductIdsAsync omits a product expiring one day after the cutoff")]
    public async Task Omits_Product_Expiring_After_Window()
    {
        var productId = Guid.NewGuid();
        var stocks = new FakeProductStockRepository();
        stocks.Items.Add(StockWithExpiry(productId, Today.AddDays(6)));
        var adapter = new MealPlanExpiringStockReaderAdapter(stocks, new FakeTenantContext(Household.Value));

        var result = await adapter.GetExpiringProductIdsAsync(Today, withinDays: 5);

        Assert.DoesNotContain(productId, result);
    }

    [Fact(DisplayName = "GetExpiringProductIdsAsync returns a product expiring today (the lower edge of the window)")]
    public async Task Returns_Product_Expiring_Today()
    {
        var productId = Guid.NewGuid();
        var stocks = new FakeProductStockRepository();
        stocks.Items.Add(StockWithExpiry(productId, Today));
        var adapter = new MealPlanExpiringStockReaderAdapter(stocks, new FakeTenantContext(Household.Value));

        var result = await adapter.GetExpiringProductIdsAsync(Today, withinDays: 5);

        Assert.Contains(productId, result);
    }

    [Fact(DisplayName = "GetExpiringProductIdsAsync omits a product whose soonest lot already expired before today")]
    public async Task Omits_Already_Expired_Product()
    {
        var productId = Guid.NewGuid();
        var stocks = new FakeProductStockRepository();
        stocks.Items.Add(StockWithExpiry(productId, Today.AddDays(-1)));
        var adapter = new MealPlanExpiringStockReaderAdapter(stocks, new FakeTenantContext(Household.Value));

        var result = await adapter.GetExpiringProductIdsAsync(Today, withinDays: 5);

        Assert.DoesNotContain(productId, result);
    }

    [Fact(DisplayName = "GetExpiringProductIdsAsync omits a product with no expiry date on any lot")]
    public async Task Omits_Product_With_No_Expiry()
    {
        var productId = Guid.NewGuid();
        var stocks = new FakeProductStockRepository();
        stocks.Items.Add(StockWithExpiry(productId, null));
        var adapter = new MealPlanExpiringStockReaderAdapter(stocks, new FakeTenantContext(Household.Value));

        var result = await adapter.GetExpiringProductIdsAsync(Today, withinDays: 5);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "GetExpiringProductIdsAsync returns empty when the tenant carries no household")]
    public async Task Returns_Empty_When_No_Household()
    {
        var stocks = new FakeProductStockRepository();
        stocks.Items.Add(StockWithExpiry(Guid.NewGuid(), Today.AddDays(1)));
        var adapter = new MealPlanExpiringStockReaderAdapter(stocks, new FakeTenantContext(null));

        var result = await adapter.GetExpiringProductIdsAsync(Today, withinDays: 5);

        Assert.Empty(result);
    }
}
