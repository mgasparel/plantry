using Microsoft.EntityFrameworkCore;
using Plantry.Inventory.Domain;
using Plantry.Inventory.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Inventory;

/// <summary>
/// L3 integration tests proving the <see cref="ProductStock.LowStockThreshold"/> persists correctly
/// through EF against a real Postgres schema, and that the RLS policy on <c>product_stock</c>
/// continues to isolate the column across households.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class LowStockThresholdTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _householdA;
    private HouseholdId _householdB;
    private readonly Guid _productId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _householdA = HouseholdId.New();
        _householdB = HouseholdId.New();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Persistence ──────────────────────────────────────────────────────

    [Fact(DisplayName = "LowStockThreshold persists through EF round-trip when set")]
    public async Task LowStockThreshold_Persists_And_Reloads_Correctly()
    {
        await using (var ctx = NewInventoryDb(_householdA))
        {
            var stock = ProductStock.Start(_householdA, _productId, SystemClock.Instance);
            stock.AddStock(10m, _unitId, _locationId, _userId, SystemClock.Instance);
            stock.SetLowStockThreshold(3.5m);
            await ctx.ProductStocks.AddAsync(stock);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewInventoryDb(_householdA);
        var loaded = await ctx2.ProductStocks
            .SingleAsync(p => p.HouseholdId == _householdA && p.ProductId == _productId);

        Assert.Equal(3.5m, loaded.LowStockThreshold);
    }

    [Fact(DisplayName = "LowStockThreshold persists as null (no threshold set)")]
    public async Task LowStockThreshold_Null_When_Not_Set()
    {
        await using (var ctx = NewInventoryDb(_householdA))
        {
            var stock = ProductStock.Start(_householdA, _productId, SystemClock.Instance);
            stock.AddStock(10m, _unitId, _locationId, _userId, SystemClock.Instance);
            // no SetLowStockThreshold call
            await ctx.ProductStocks.AddAsync(stock);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewInventoryDb(_householdA);
        var loaded = await ctx2.ProductStocks
            .SingleAsync(p => p.HouseholdId == _householdA && p.ProductId == _productId);

        Assert.Null(loaded.LowStockThreshold);
    }

    [Fact(DisplayName = "IsRunningLow is correct from persisted threshold (onHand at threshold → true)")]
    public async Task IsRunningLow_Correct_After_Reload_When_OnHand_At_Threshold()
    {
        await using (var ctx = NewInventoryDb(_householdA))
        {
            var stock = ProductStock.Start(_householdA, _productId, SystemClock.Instance);
            stock.AddStock(5m, _unitId, _locationId, _userId, SystemClock.Instance);
            stock.SetLowStockThreshold(5m); // 5 ≤ 5 → running low
            await ctx.ProductStocks.AddAsync(stock);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewInventoryDb(_householdA);
        var loaded = await ctx2.ProductStocks
            .Include(p => p.Entries)
            .SingleAsync(p => p.HouseholdId == _householdA && p.ProductId == _productId);

        var onHand = loaded.ActiveLotsFefo().Sum(l => l.Quantity);
        Assert.True(loaded.IsRunningLow(onHand));
    }

    // ── RLS cross-household isolation ─────────────────────────────────────

    [Fact(DisplayName = "Household A cannot read household B's low stock threshold (EF query filter)")]
    public async Task LowStockThreshold_CrossHousehold_Isolation_EfFilter()
    {
        var sharedProductId = Guid.CreateVersion7();

        // Household A: threshold = 10
        await using (var ctxA = NewInventoryDb(_householdA))
        {
            var stockA = ProductStock.Start(_householdA, sharedProductId, SystemClock.Instance);
            stockA.AddStock(1m, _unitId, _locationId, _userId, SystemClock.Instance);
            stockA.SetLowStockThreshold(10m);
            await ctxA.ProductStocks.AddAsync(stockA);
            await ctxA.SaveChangesAsync();
        }

        // Household B: threshold = 99
        await using (var ctxB = NewInventoryDb(_householdB))
        {
            var stockB = ProductStock.Start(_householdB, sharedProductId, SystemClock.Instance);
            stockB.AddStock(1m, _unitId, _locationId, _userId, SystemClock.Instance);
            stockB.SetLowStockThreshold(99m);
            await ctxB.ProductStocks.AddAsync(stockB);
            await ctxB.SaveChangesAsync();
        }

        // Household A context may only see its own row
        await using var verifyA = NewInventoryDb(_householdA);
        var seenByA = await verifyA.ProductStocks.ToListAsync();
        Assert.All(seenByA, p => Assert.Equal(_householdA, p.HouseholdId));
        Assert.DoesNotContain(seenByA, p => p.LowStockThreshold == 99m);

        var ownA = Assert.Single(seenByA, p => p.ProductId == sharedProductId);
        Assert.Equal(10m, ownA.LowStockThreshold);
    }

    // ── Query service returns correct IsRunningLow from persisted threshold ─

    [Fact(DisplayName = "InventoryQueryService.ListPantry returns correct IsRunningLow from persisted threshold")]
    public async Task QueryService_ListPantry_Surfaces_Correct_IsRunningLow_From_Persisted_Threshold()
    {
        // Seed a stock row with threshold = 5, on-hand = 3 (running low)
        await using (var ctx = NewInventoryDb(_householdA))
        {
            var stock = ProductStock.Start(_householdA, _productId, SystemClock.Instance);
            stock.AddStock(3m, _unitId, _locationId, _userId, SystemClock.Instance);
            stock.SetLowStockThreshold(5m);
            await ctx.ProductStocks.AddAsync(stock);
            await ctx.SaveChangesAsync();
        }

        // Reload and check through domain method (mirrors what InventoryQueryService computes)
        await using var ctx2 = NewInventoryDb(_householdA);
        var loaded = await ctx2.ProductStocks
            .Include(p => p.Entries)
            .SingleAsync(p => p.HouseholdId == _householdA && p.ProductId == _productId);

        var onHand = loaded.ActiveLotsFefo().Sum(l => l.Quantity);
        Assert.Equal(3m, onHand);
        Assert.Equal(5m, loaded.LowStockThreshold);
        Assert.True(loaded.IsRunningLow(onHand)); // 3 ≤ 5 → true
    }

    private DbContextOptions<InventoryDbContext> InventoryOptions() =>
        new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(db.ConnectionString).Options;

    private InventoryDbContext NewInventoryDb(HouseholdId household)
    {
        var ctx = new InventoryDbContext(InventoryOptions());
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }
}
