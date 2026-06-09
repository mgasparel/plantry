using Microsoft.EntityFrameworkCore;
using Plantry.Inventory.Domain;
using Plantry.Inventory.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Inventory;

/// <summary>
/// The headline L3 test for Slice 2 (PHASE-1-PLAN.md Stage B; DM-13): two concurrent
/// <see cref="ProductStock.Consume"/> calls competing for one shared lot must never over-deduct.
/// The repository's <c>SELECT … FOR UPDATE</c> on the <c>product_stock</c> root serializes the
/// writers, and the <c>xmin</c> token is the optimistic backstop on the lock-free read path.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ConsumeConcurrencyTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private readonly Guid _productId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly IQuantityConverter _converter = new IdentityConverter();

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        await using var seedDb = NewInventoryDb();
        var stock = ProductStock.Start(_household, _productId, SystemClock.Instance);
        stock.AddStock(100m, _unitId, _locationId, _userId, SystemClock.Instance);
        await seedDb.ProductStocks.AddAsync(stock);
        await seedDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "FOR UPDATE serializes two concurrent consumes over one lot — no over-deduction")]
    public async Task TwoConcurrentConsumes_OverSharedLot_DoNotOverDeduct()
    {
        // First writer takes the row lock and signals; the second is held until then, so its own
        // SELECT … FOR UPDATE blocks on the locked root and only proceeds after the first commits.
        var firstHoldsLock = new TaskCompletionSource();

        async Task<ConsumeOutcome> First()
        {
            await using var ctx = NewInventoryDb();
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var repo = new ProductStockRepository(ctx);
            var root = await repo.FindForUpdateAsync(_household, _productId);

            firstHoldsLock.SetResult();
            await Task.Delay(750); // hold the lock so the second writer is forced to wait on it

            var outcome = root!.Consume(60m, _unitId, StockReason.Consumed, _converter, _userId, SystemClock.Instance);
            await ctx.SaveChangesAsync();
            await tx.CommitAsync();
            return outcome.Value;
        }

        async Task<ConsumeOutcome> Second()
        {
            await firstHoldsLock.Task;
            await using var ctx = NewInventoryDb();
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var repo = new ProductStockRepository(ctx);
            var root = await repo.FindForUpdateAsync(_household, _productId); // blocks until First commits

            var outcome = root!.Consume(60m, _unitId, StockReason.Consumed, _converter, _userId, SystemClock.Instance);
            await ctx.SaveChangesAsync();
            await tx.CommitAsync();
            return outcome.Value;
        }

        var firstTask = First();
        var secondTask = Second();
        var first = await firstTask;
        var second = await secondTask;

        // First sees the full 100, takes 60. Second then sees only 40, takes 40 and reports 20 short.
        Assert.False(first.HasShortfall);
        Assert.Equal(60m, first.Deductions.Sum(d => d.Amount));
        Assert.Equal(20m, second.ShortfallAmount);
        Assert.Equal(40m, second.Deductions.Sum(d => d.Amount));

        // Ground truth in the database: exactly 100 removed, lot depleted, never negative.
        await using var verifyDb = NewInventoryDb();
        var stock = await verifyDb.ProductStocks
            .Include(p => p.Entries)
            .Include(p => p.Journal)
            .SingleAsync(p => p.HouseholdId == _household && p.ProductId == _productId);

        var lot = Assert.Single(stock.Entries);
        Assert.Equal(0m, lot.Quantity);
        Assert.True(lot.IsDepleted);
        Assert.Equal(0m, stock.Journal.Sum(j => j.Delta)); // +100 intake, −60, −40
    }

    [Fact(DisplayName = "Stale xmin on the lock-free read path throws DbUpdateConcurrencyException")]
    public async Task StaleXmin_OnConcurrentDetachedEdit_Throws()
    {
        await using var ctxA = NewInventoryDb();
        await using var ctxB = NewInventoryDb();

        var rootA = await ctxA.ProductStocks.Include(p => p.Entries)
            .SingleAsync(p => p.HouseholdId == _household && p.ProductId == _productId);
        var rootB = await ctxB.ProductStocks.Include(p => p.Entries)
            .SingleAsync(p => p.HouseholdId == _household && p.ProductId == _productId);

        // A commits first, bumping the root's xmin.
        rootA.Consume(10m, _unitId, StockReason.Consumed, _converter, _userId, SystemClock.Instance);
        await ctxA.SaveChangesAsync();

        // B still holds the pre-commit xmin; its UPDATE matches no row and EF surfaces the conflict.
        rootB.Consume(10m, _unitId, StockReason.Consumed, _converter, _userId, SystemClock.Instance);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => ctxB.SaveChangesAsync());
    }

    private DbContextOptions<InventoryDbContext> InventoryOptions() =>
        new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(db.ConnectionString).Options;

    private InventoryDbContext NewInventoryDb()
    {
        var ctx = new InventoryDbContext(InventoryOptions());
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    /// <summary>Single-unit identity converter — these scenarios never cross units.</summary>
    private sealed class IdentityConverter : IQuantityConverter
    {
        public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId) => amount;
    }
}
