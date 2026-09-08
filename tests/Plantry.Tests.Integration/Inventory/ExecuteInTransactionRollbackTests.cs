using Microsoft.EntityFrameworkCore;
using Plantry.Pantry.Domain;
using Plantry.Pantry.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Inventory;

/// <summary>
/// L3 coverage for plantry-bxzh's core infrastructure fix: <see cref="ProductStockRepository.ExecuteInTransactionAsync{T}"/>
/// must roll back a write that flushed inside its delegate when the delegate's own return value is a
/// failed <see cref="Result"/>/<see cref="Result{T}"/> — not just on a thrown exception. Commands return
/// failures rather than throwing, so the pre-fix unconditional CommitAsync left a durable partial effect
/// (a stock_entry/stock_journal_entry pair) behind a response the caller saw as an error. A unit test
/// against the in-memory <c>FakeProductStockRepository</c> cannot catch this — that fake's
/// <c>ExecuteInTransactionAsync</c> just invokes the delegate inline (see its doc comment); this defect
/// lives in the real EF/Postgres transaction boundary, so it needs a real database.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ExecuteInTransactionRollbackTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private readonly Guid _productId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "A write that flushed inside the delegate is rolled back when the delegate returns a failed Result")]
    public async Task Delegate_Returning_Failed_Result_Rolls_Back_Its_Own_Flushed_Write()
    {
        await using var ctx = NewInventoryDb();
        var repo = new ProductStockRepository(ctx);

        var result = await repo.ExecuteInTransactionAsync<Result<int>>(async ct =>
        {
            // Mint the aggregate and flush it — exactly the shape of RecordCountCommand's
            // first-ever-stock branch (AddStock, then SaveChangesAsync via TryAddAndSaveAsync)
            // followed by a downstream failure the caller reports back as an error.
            var stock = ProductStock.Start(_household, _productId, SystemClock.Instance);
            stock.AddStock(12m, _unitId, _locationId, _userId, SystemClock.Instance);
            await ctx.ProductStocks.AddAsync(stock, ct);
            await ctx.SaveChangesAsync(ct);

            return Result<int>.Failure(Error.Custom("Test.Simulated", "downstream failure after a flushed write"));
        });

        Assert.True(result.IsFailure);

        await using var verifyDb = NewInventoryDb();
        var persisted = await verifyDb.ProductStocks
            .Include(p => p.Entries)
            .Include(p => p.Journal)
            .SingleOrDefaultAsync(p => p.HouseholdId == _household && p.ProductId == _productId);

        // The whole aggregate — root, lot, and journal row — must be gone; nothing survives a
        // rolled-back transaction (plantry-bxzh's reported symptom: a stock_entry +
        // stock_journal_entry committed despite the command reporting failure).
        Assert.Null(persisted);
    }

    [Fact(DisplayName = "A write inside a delegate that returns success is still committed (no regression)")]
    public async Task Delegate_Returning_Success_Still_Commits()
    {
        await using var ctx = NewInventoryDb();
        var repo = new ProductStockRepository(ctx);

        var result = await repo.ExecuteInTransactionAsync<Result<int>>(async ct =>
        {
            var stock = ProductStock.Start(_household, _productId, SystemClock.Instance);
            stock.AddStock(12m, _unitId, _locationId, _userId, SystemClock.Instance);
            await ctx.ProductStocks.AddAsync(stock, ct);
            await ctx.SaveChangesAsync(ct);
            return Result<int>.Success(1);
        });

        Assert.True(result.IsSuccess);

        await using var verifyDb = NewInventoryDb();
        var persisted = await verifyDb.ProductStocks
            .Include(p => p.Entries)
            .SingleOrDefaultAsync(p => p.HouseholdId == _household && p.ProductId == _productId);

        Assert.NotNull(persisted);
        Assert.Single(persisted!.Entries);
    }

    [Fact(DisplayName = "A thrown exception inside the delegate still rolls back (unchanged behaviour)")]
    public async Task Delegate_Throwing_Still_Rolls_Back()
    {
        await using var ctx = NewInventoryDb();
        var repo = new ProductStockRepository(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repo.ExecuteInTransactionAsync<int>(async ct =>
            {
                var stock = ProductStock.Start(_household, _productId, SystemClock.Instance);
                stock.AddStock(12m, _unitId, _locationId, _userId, SystemClock.Instance);
                await ctx.ProductStocks.AddAsync(stock, ct);
                await ctx.SaveChangesAsync(ct);
                throw new InvalidOperationException("boom");
            }));

        await using var verifyDb = NewInventoryDb();
        var persisted = await verifyDb.ProductStocks
            .SingleOrDefaultAsync(p => p.HouseholdId == _household && p.ProductId == _productId);
        Assert.Null(persisted);
    }

    [Fact(DisplayName = "A non-generic Result failure also rolls back its delegate's flushed write")]
    public async Task NonGeneric_Result_Failure_Also_Rolls_Back()
    {
        await using var ctx = NewInventoryDb();
        var repo = new ProductStockRepository(ctx);

        var result = await repo.ExecuteInTransactionAsync<Result>(async ct =>
        {
            var stock = ProductStock.Start(_household, _productId, SystemClock.Instance);
            stock.AddStock(12m, _unitId, _locationId, _userId, SystemClock.Instance);
            await ctx.ProductStocks.AddAsync(stock, ct);
            await ctx.SaveChangesAsync(ct);
            return Result.Failure(Error.Custom("Test.Simulated", "downstream failure"));
        });

        Assert.True(result.IsFailure);

        await using var verifyDb = NewInventoryDb();
        var persisted = await verifyDb.ProductStocks
            .SingleOrDefaultAsync(p => p.HouseholdId == _household && p.ProductId == _productId);
        Assert.Null(persisted);
    }

    [Fact(DisplayName = "TryAddAndSaveAsync's savepoint lets the ambient transaction survive a duplicate-key INSERT")]
    public async Task TryAddAndSaveAsync_Savepoint_Survives_Duplicate_Key_Insert_Inside_Ambient_Transaction()
    {
        // Pre-existing root for (household, productId) — the primary key is (household_id,
        // product_id) (see the InitialPantrySchema migration), so a second INSERT for the same
        // pair is a duplicate-key violation, exactly the race RecordCountCommand's first-ever-
        // stock branch handles by falling back to the delta path.
        await using var seedDb = NewInventoryDb();
        var existing = ProductStock.Start(_household, _productId, SystemClock.Instance);
        existing.AddStock(5m, _unitId, _locationId, _userId, SystemClock.Instance);
        await seedDb.ProductStocks.AddAsync(existing);
        await seedDb.SaveChangesAsync();

        await using var ctx = NewInventoryDb();
        var repo = new ProductStockRepository(ctx);

        await repo.ExecuteInTransactionAsync(async ct =>
        {
            var racer = ProductStock.Start(_household, _productId, SystemClock.Instance);
            racer.AddStock(99m, _unitId, _locationId, _userId, SystemClock.Instance);

            // Without the savepoint fix, this failed INSERT aborts the whole Postgres
            // transaction (25P02) and the FindForUpdateAsync below throws instead of returning
            // the pre-existing root.
            var inserted = await repo.TryAddAndSaveAsync(racer, ct);
            Assert.False(inserted);

            // The ambient transaction (and its row lock capability) must still be usable —
            // this is exactly what RecordCountCommand's race fallback does next.
            var reloaded = await repo.FindForUpdateAsync(_household, _productId, ct);
            Assert.NotNull(reloaded);
            Assert.Single(reloaded!.Entries);
            Assert.Equal(5m, reloaded.Entries.Single().Quantity); // the racer's insert never landed

            return true;
        });
    }

    private DbContextOptions<PantryDbContext> InventoryOptions() =>
        new DbContextOptionsBuilder<PantryDbContext>().UseNpgsql(db.ConnectionString).Options;

    private PantryDbContext NewInventoryDb()
    {
        var ctx = new PantryDbContext(InventoryOptions());
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }
}
