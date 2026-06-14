using Microsoft.EntityFrameworkCore;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.Inventory.Domain;
using Plantry.Inventory.Infrastructure;
using Plantry.Recipes.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.Inventory;
using Plantry.Web.Recipes;
using Xunit;
using CatalogUnit = Plantry.Catalog.Domain.Unit;
using InventoryProductStock = Plantry.Inventory.Domain.ProductStock;

namespace Plantry.Tests.Integration.Recipes;

/// <summary>
/// L3 integration test proving the <see cref="InventoryConsumerAdapter"/> wiring:
/// calling the adapter consumes stock via FEFO from the real Inventory aggregate, writes a signed
/// journal row stamped with <c>source_type = Cook</c> and <c>source_ref = cookEventId</c>, and
/// reports any shortfall (ADR-011 / recipes-domain-model.md §8, P2-3b acceptance criteria).
/// FEFO deduction and unit conversion correctness are covered by the Inventory unit tests;
/// this test only proves the adapter wiring and source stamping.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class InventoryConsumerAdapterTests(PostgresFixture db) : IAsyncLifetime
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

        // Seed a unit and a stock-holding product in Catalog so Inventory's converter can resolve it.
        await using var catalogDb = NewCatalogDb();
        var grams = CatalogUnit.Create(_household, "g", "grams", Dimension.Mass, 1m, isBase: true);
        await catalogDb.Units.AddAsync(grams);
        var product = Product.Create(_household, "Flour", grams.Id, Clock);
        await catalogDb.Products.AddAsync(product);
        await catalogDb.SaveChangesAsync();
        _unitId = grams.Id.Value;
        _productId = product.Id.Value;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact(DisplayName = "Consume decrements FEFO lot and writes Cook-sourced journal row with cookEventId")]
    public async Task Consume_Decrements_Lot_And_Stamps_Journal_Cook_Source()
    {
        var cookEventId = Guid.CreateVersion7();

        // Seed one lot of 500 g.
        await using (var invDb = NewInventoryDb())
        {
            var stock = InventoryProductStock.Start(_household, _productId, Clock);
            stock.AddStock(500m, _unitId, locationId: Guid.CreateVersion7(), _userId, Clock);
            await invDb.ProductStocks.AddAsync(stock);
            await invDb.SaveChangesAsync();
        }

        var result = await BuildAdapter().ConsumeAsync(
            _productId, quantity: 200m, _unitId,
            ConsumeReason.Recipe, cookEventId, _userId);

        // Shortfall is zero — 500 g available, 200 g consumed.
        Assert.False(result.HasShortfall);
        Assert.Equal(0m, result.ShortfallAmount);

        // Verify journal: one Purchase row (intake) + one Consumed row (cook).
        await using var verify = NewInventoryDb();
        var loaded = await verify.ProductStocks
            .Include(p => p.Entries)
            .Include(p => p.Journal)
            .SingleAsync(p => p.ProductId == _productId);

        var consumeRow = loaded.Journal.Single(j => j.Reason == StockReason.Consumed);
        Assert.Equal(-200m, consumeRow.Delta);
        Assert.Equal(StockSourceType.Cook, consumeRow.SourceType);
        Assert.Equal(cookEventId, consumeRow.SourceRef);

        // The lot was deducted from — 300 g should remain.
        var lot = Assert.Single(loaded.Entries);
        Assert.Equal(300m, lot.Quantity);
        Assert.True(lot.IsActive);
    }

    [Fact(DisplayName = "Consume respects FEFO order across two lots")]
    public async Task Consume_Respects_FEFO_Order()
    {
        var soonExpiry = new DateOnly(2026, 7, 1);
        var laterExpiry = new DateOnly(2026, 8, 1);
        var cookEventId = Guid.CreateVersion7();

        // Seed two lots: soonest expiry first in FEFO.
        await using (var invDb = NewInventoryDb())
        {
            var stock = InventoryProductStock.Start(_household, _productId, Clock);
            stock.AddStock(100m, _unitId, locationId: Guid.CreateVersion7(), _userId, Clock, expiryDate: laterExpiry);
            stock.AddStock(100m, _unitId, locationId: Guid.CreateVersion7(), _userId, Clock, expiryDate: soonExpiry);
            await invDb.ProductStocks.AddAsync(stock);
            await invDb.SaveChangesAsync();
        }

        // Consume 150 g — should drain the soonest-expiry lot (100 g) then take 50 g from the later.
        var result = await BuildAdapter().ConsumeAsync(
            _productId, quantity: 150m, _unitId,
            ConsumeReason.Recipe, cookEventId, _userId);

        Assert.False(result.HasShortfall);

        await using var verify = NewInventoryDb();
        var loaded = await verify.ProductStocks
            .Include(p => p.Entries)
            .Include(p => p.Journal)
            .SingleAsync(p => p.ProductId == _productId);

        // Two consume journal rows (one per lot touched), both stamped Cook.
        var consumeRows = loaded.Journal.Where(j => j.Reason == StockReason.Consumed).ToList();
        Assert.Equal(2, consumeRows.Count);
        Assert.All(consumeRows, j => Assert.Equal(StockSourceType.Cook, j.SourceType));
        Assert.All(consumeRows, j => Assert.Equal(cookEventId, j.SourceRef));
        Assert.Equal(-150m, consumeRows.Sum(j => j.Delta));

        // Soonest-expiry lot is depleted; later-expiry lot has 50 g left.
        var fefo = loaded.ActiveLotsFefo().ToList();
        Assert.Single(fefo); // only the later-expiry lot remains active
        Assert.Equal(50m, fefo[0].Quantity);
        Assert.Equal(laterExpiry, fefo[0].ExpiryDate);
    }

    [Fact(DisplayName = "Consume reports shortfall when stock is insufficient — never over-deducts")]
    public async Task Consume_Reports_Shortfall_When_Stock_Is_Insufficient()
    {
        var cookEventId = Guid.CreateVersion7();

        // Seed only 100 g; try to consume 300 g.
        await using (var invDb = NewInventoryDb())
        {
            var stock = InventoryProductStock.Start(_household, _productId, Clock);
            stock.AddStock(100m, _unitId, locationId: Guid.CreateVersion7(), _userId, Clock);
            await invDb.ProductStocks.AddAsync(stock);
            await invDb.SaveChangesAsync();
        }

        var result = await BuildAdapter().ConsumeAsync(
            _productId, quantity: 300m, _unitId,
            ConsumeReason.Recipe, cookEventId, _userId);

        Assert.True(result.HasShortfall);
        Assert.Equal(200m, result.ShortfallAmount); // 300 requested − 100 available
        Assert.Equal(_unitId, result.RequestUnitId);

        // The lot was fully drained (no over-deduction).
        await using var verify = NewInventoryDb();
        var loaded = await verify.ProductStocks
            .Include(p => p.Entries)
            .SingleAsync(p => p.ProductId == _productId);

        var lot = Assert.Single(loaded.Entries);
        Assert.Equal(0m, lot.Quantity);
        Assert.True(lot.IsDepleted);
    }

    [Fact(DisplayName = "Consume throws when product has no stock record")]
    public async Task Consume_Throws_When_No_Stock_Record_Exists()
    {
        var cookEventId = Guid.CreateVersion7();
        var unknownProductId = Guid.CreateVersion7();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BuildAdapter().ConsumeAsync(
                unknownProductId, quantity: 50m, _unitId,
                ConsumeReason.Recipe, cookEventId, _userId));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private IInventoryConsumer BuildAdapter()
    {
        var invDb = NewInventoryDb();
        var catDb = NewCatalogDb();
        var productRepo = new Plantry.Catalog.Infrastructure.ProductRepository(catDb);
        var unitRepo = new Plantry.Catalog.Infrastructure.UnitRepository(catDb);
        var conversions = new CatalogConversionProvider(productRepo, unitRepo);
        var stocks = new ProductStockRepository(invDb);
        var tenant = new TestTenant(_household.Value);
        return new InventoryConsumerAdapter(stocks, conversions, Clock, tenant);
    }

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
