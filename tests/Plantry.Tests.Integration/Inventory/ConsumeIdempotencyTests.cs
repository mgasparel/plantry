using Microsoft.EntityFrameworkCore;
using Plantry.Catalog.Infrastructure;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.Inventory.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.Inventory;
using Xunit;
using CatalogUnit = Plantry.Catalog.Domain.Unit;
using InventoryProductStock = Plantry.Inventory.Domain.ProductStock;

namespace Plantry.Tests.Integration.Inventory;

/// <summary>
/// L3 integration tests proving the <see cref="ProductStock.Consume"/> idempotency token
/// (plantry-292a, acceptance criteria):
/// <list type="bullet">
/// <item>Re-driving an already-applied <c>sourceLineRef</c> token writes no further journal rows
/// and does not change stock.</item>
/// <item>The manual-consume path (null <c>sourceLineRef</c>) is unaffected — a second call with
/// null does apply normally (regression guard).</item>
/// </list>
/// Tests run against a real Postgres schema (migration applied) so the column and index are
/// verified end-to-end.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ConsumeIdempotencyTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly IClock Clock = SystemClock.Instance;
    private HouseholdId _household;
    private readonly Guid _userId = Guid.CreateVersion7();
    private Guid _productId;
    private Guid _unitId;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        // Seed a unit and a tracked product in Catalog so the conversion provider resolves.
        await using var catalogDb = NewCatalogDb();
        var grams = CatalogUnit.Create(_household, "g", "grams", Plantry.Catalog.Domain.Dimension.Mass, 1m, isBase: true);
        await catalogDb.Units.AddAsync(grams);
        var product = Plantry.Catalog.Domain.Product.Create(_household, "Flour", grams.Id, Clock);
        await catalogDb.Products.AddAsync(product);
        await catalogDb.SaveChangesAsync();
        _unitId = grams.Id.Value;
        _productId = product.Id.Value;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Idempotency ───────────────────────────────────────────────────────────

    [Fact(DisplayName = "Re-driving the same sourceLineRef token writes no journal rows and leaves stock unchanged")]
    public async Task Consume_SameSourceLineRef_IsNoOp_OnRedrive()
    {
        var cookEventId = Guid.CreateVersion7();
        var lineRef = Guid.CreateVersion7();

        // Seed 500 g.
        await using (var invDb = NewInventoryDb())
        {
            var stock = InventoryProductStock.Start(_household, _productId, Clock);
            stock.AddStock(500m, _unitId, locationId: Guid.CreateVersion7(), _userId, Clock);
            await invDb.ProductStocks.AddAsync(stock);
            await invDb.SaveChangesAsync();
        }

        // First consume: 200 g.
        await RunConsumeAsync(cookEventId, lineRef, amount: 200m);

        // Verify state after first consume.
        await using (var verify = NewInventoryDb())
        {
            var loaded = await LoadWithHistoryAsync(verify);
            var lot = Assert.Single(loaded.Entries);
            Assert.Equal(300m, lot.Quantity); // 500 − 200

            var removalRows = loaded.Journal.Where(j => j.Reason == StockReason.Consumed).ToList();
            Assert.Single(removalRows); // exactly one removal row written
            Assert.Equal(lineRef, removalRows[0].SourceLineRef);
            Assert.Equal(cookEventId, removalRows[0].SourceRef);
        }

        // Re-drive with the same token: must be a no-op.
        await RunConsumeAsync(cookEventId, lineRef, amount: 200m);

        // Verify that stock is unchanged and no new journal rows were written.
        await using (var verify = NewInventoryDb())
        {
            var loaded = await LoadWithHistoryAsync(verify);
            var lot = Assert.Single(loaded.Entries);
            Assert.Equal(300m, lot.Quantity); // still 300 g — no second deduction

            var removalRows = loaded.Journal.Where(j => j.Reason == StockReason.Consumed).ToList();
            Assert.Single(removalRows); // still exactly one — idempotent
        }
    }

    [Fact(DisplayName = "A different sourceLineRef on the same cook event applies a new deduction")]
    public async Task Consume_DifferentSourceLineRef_AppliesNormally()
    {
        var cookEventId = Guid.CreateVersion7();
        var lineRefA = Guid.CreateVersion7();
        var lineRefB = Guid.CreateVersion7();

        // Seed 500 g.
        await using (var invDb = NewInventoryDb())
        {
            var stock = InventoryProductStock.Start(_household, _productId, Clock);
            stock.AddStock(500m, _unitId, locationId: Guid.CreateVersion7(), _userId, Clock);
            await invDb.ProductStocks.AddAsync(stock);
            await invDb.SaveChangesAsync();
        }

        // Two distinct lines of the same cook event — each is its own consume.
        await RunConsumeAsync(cookEventId, lineRefA, amount: 100m);
        await RunConsumeAsync(cookEventId, lineRefB, amount: 150m);

        await using var verify = NewInventoryDb();
        var loaded = await LoadWithHistoryAsync(verify);
        var lot = Assert.Single(loaded.Entries);
        Assert.Equal(250m, lot.Quantity); // 500 − 100 − 150

        var removalRows = loaded.Journal.Where(j => j.Reason == StockReason.Consumed).ToList();
        Assert.Equal(2, removalRows.Count); // one row per distinct line ref
        Assert.Contains(removalRows, j => j.SourceLineRef == lineRefA);
        Assert.Contains(removalRows, j => j.SourceLineRef == lineRefB);
    }

    [Fact(DisplayName = "Manual consume (null sourceLineRef) is not affected by idempotency — a second call applies")]
    public async Task Consume_NullSourceLineRef_IsNotShortCircuited()
    {
        // Seed 500 g.
        await using (var invDb = NewInventoryDb())
        {
            var stock = InventoryProductStock.Start(_household, _productId, Clock);
            stock.AddStock(500m, _unitId, locationId: Guid.CreateVersion7(), _userId, Clock);
            await invDb.ProductStocks.AddAsync(stock);
            await invDb.SaveChangesAsync();
        }

        // Two manual consumes (null sourceLineRef) — regression guard: both must apply.
        await RunManualConsumeAsync(amount: 50m);
        await RunManualConsumeAsync(amount: 50m);

        await using var verify = NewInventoryDb();
        var loaded = await LoadWithHistoryAsync(verify);
        var lot = Assert.Single(loaded.Entries);
        Assert.Equal(400m, lot.Quantity); // 500 − 50 − 50

        var removalRows = loaded.Journal.Where(j => j.Reason == StockReason.Consumed).ToList();
        Assert.Equal(2, removalRows.Count);
        Assert.All(removalRows, j => Assert.Null(j.SourceLineRef));
    }

    [Fact(DisplayName = "sourceLineRef is persisted on each per-lot journal row in a multi-lot consume")]
    public async Task Consume_SourceLineRef_PersistedOnEachJournalRow_MultiLot()
    {
        var cookEventId = Guid.CreateVersion7();
        var lineRef = Guid.CreateVersion7();

        // Seed two lots: 100 g each.
        await using (var invDb = NewInventoryDb())
        {
            var stock = InventoryProductStock.Start(_household, _productId, Clock);
            stock.AddStock(100m, _unitId, locationId: Guid.CreateVersion7(), _userId, Clock,
                expiryDate: new DateOnly(2026, 7, 1));
            stock.AddStock(100m, _unitId, locationId: Guid.CreateVersion7(), _userId, Clock,
                expiryDate: new DateOnly(2026, 8, 1));
            await invDb.ProductStocks.AddAsync(stock);
            await invDb.SaveChangesAsync();
        }

        // Consume 150 g (crosses both lots).
        await RunConsumeAsync(cookEventId, lineRef, amount: 150m);

        await using var verify = NewInventoryDb();
        var loaded = await LoadWithHistoryAsync(verify);

        var removalRows = loaded.Journal.Where(j => j.Reason == StockReason.Consumed).ToList();
        Assert.Equal(2, removalRows.Count); // one per lot touched
        Assert.All(removalRows, j => Assert.Equal(lineRef, j.SourceLineRef));
        Assert.All(removalRows, j => Assert.Equal(cookEventId, j.SourceRef));

        // Re-drive: both rows have the token, so a re-scan finds it and short-circuits.
        await RunConsumeAsync(cookEventId, lineRef, amount: 150m);

        await using var verify2 = NewInventoryDb();
        var loaded2 = await LoadWithHistoryAsync(verify2);
        Assert.Equal(2, loaded2.Journal.Count(j => j.Reason == StockReason.Consumed)); // still 2
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task RunConsumeAsync(Guid cookEventId, Guid sourceLineRef, decimal amount)
    {
        await using var invDb = NewInventoryDb();
        await using var catDb = NewCatalogDb();
        var productRepo = new Plantry.Catalog.Infrastructure.ProductRepository(catDb);
        var unitRepo = new Plantry.Catalog.Infrastructure.UnitRepository(catDb);
        var conversions = new CatalogConversionProvider(productRepo, unitRepo);
        var stocks = new ProductStockRepository(invDb);
        var tenant = new TestTenant(_household.Value);

        var command = new ConsumeStockCommand(
            _productId, amount, _unitId,
            StockReason.Consumed, _userId,
            targetEntryId: null,
            sourceRef: cookEventId,
            stocks, conversions, Clock, tenant,
            StockSourceType.Cook,
            sourceLineRef: sourceLineRef);

        var result = await command.ExecuteAsync();
        Assert.True(result.IsSuccess, $"Consume failed: {result.Error?.Description}");
    }

    private async Task RunManualConsumeAsync(decimal amount)
    {
        await using var invDb = NewInventoryDb();
        await using var catDb = NewCatalogDb();
        var productRepo = new Plantry.Catalog.Infrastructure.ProductRepository(catDb);
        var unitRepo = new Plantry.Catalog.Infrastructure.UnitRepository(catDb);
        var conversions = new CatalogConversionProvider(productRepo, unitRepo);
        var stocks = new ProductStockRepository(invDb);
        var tenant = new TestTenant(_household.Value);

        // Manual path: no sourceLineRef (null).
        var command = new ConsumeStockCommand(
            _productId, amount, _unitId,
            StockReason.Consumed, _userId,
            targetEntryId: null, sourceRef: null,
            stocks, conversions, Clock, tenant);

        var result = await command.ExecuteAsync();
        Assert.True(result.IsSuccess, $"Consume failed: {result.Error?.Description}");
    }

    private Task<InventoryProductStock> LoadWithHistoryAsync(InventoryDbContext ctx) =>
        ctx.ProductStocks
            .Include(p => p.Entries)
            .Include(p => p.Journal)
            .SingleAsync(p => p.HouseholdId == _household && p.ProductId == _productId);

    private InventoryDbContext NewInventoryDb()
    {
        var ctx = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private CatalogDbContext NewCatalogDb()
    {
        var ctx = new CatalogDbContext(
            new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private sealed class TestTenant(Guid household) : ITenantContext
    {
        public Guid? HouseholdId { get; } = household;
    }
}
