using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plantry.Inventory.Domain;
using Plantry.Inventory.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Inventory;

/// <summary>
/// L3 integration tests for P4-3 Take Stock read side:
/// <list type="bullet">
/// <item>TS-S2: <c>ix_stock_entry_by_location</c> partial index applies cleanly and filters depleted rows.</item>
/// <item>Union listing: both branch A (active stock) and branch B (no stock) are physically supported
///   by the schema (correct per-(product, location) recorded sums; depleted rows ignored).</item>
/// <item>ListNoLocationRows: active stock in any location is visible per household (RLS enforces isolation
///   at query time).</item>
/// <item>RLS isolation: household A cannot read household B's stock entries via the location index path.</item>
/// </list>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class TakeStockIndexTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private readonly Guid _productA = Guid.CreateVersion7();
    private readonly Guid _productB = Guid.CreateVersion7();
    private readonly Guid _unitId   = Guid.CreateVersion7();
    private readonly Guid _locA     = Guid.CreateVersion7(); // Pantry
    private readonly Guid _locB     = Guid.CreateVersion7(); // Fridge
    private readonly Guid _userId   = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Index: applies cleanly and filters depleted rows ──────────────────────

    [Fact(DisplayName = "ix_stock_entry_by_location exists and filters out depleted lots")]
    public async Task LocationIndex_ExistsAndFiltersDepleted()
    {
        // Seed: one lot fully depleted + one active lot in the same location.
        // FEFO (no expiry → nulls last → ordered by created_at ASC): the first-created
        // 200m lot is consumed entirely before touching the second 500m lot.
        await using (var seedDb = NewInventoryDb(_household))
        {
            var stock = ProductStock.Start(_household, _productA, SystemClock.Instance);
            stock.AddStock(200m, _unitId, _locA, _userId, SystemClock.Instance); // first lot — will be depleted
            stock.AddStock(500m, _unitId, _locA, _userId, SystemClock.Instance); // second lot — stays active
            // Consuming exactly 200m depletes the first lot only (FEFO takes from earliest-created first).
            stock.Consume(200m, _unitId, StockReason.Consumed, new IdentityConverter(), _userId, SystemClock.Instance);
            await seedDb.ProductStocks.AddAsync(stock);
            await seedDb.SaveChangesAsync();
        }

        // Query with the partial-index predicate — confirms only the active (non-depleted) lot is visible.
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM inventory.stock_entry
            WHERE household_id = @hid
              AND location_id   = @loc
              AND product_id    = @pid
              AND depleted_at IS NULL
            """;
        cmd.Parameters.AddWithValue("hid", _household.Value);
        cmd.Parameters.AddWithValue("loc", _locA);
        cmd.Parameters.AddWithValue("pid", _productA);

        var count = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(1L, count); // only the 500m active lot; the 200m depleted lot is excluded
    }

    // ── Union listing: branch A — correct recorded sum per (product, location) ─

    [Fact(DisplayName = "Union branch A: active lots sum correctly per (product, location)")]
    public async Task UnionBranchA_ActiveLotsSum()
    {
        await using (var seedDb = NewInventoryDb(_household))
        {
            var stock = ProductStock.Start(_household, _productA, SystemClock.Instance);
            stock.AddStock(300m, _unitId, _locA, _userId, SystemClock.Instance);
            stock.AddStock(200m, _unitId, _locA, _userId, SystemClock.Instance);
            await seedDb.ProductStocks.AddAsync(stock);
            await seedDb.SaveChangesAsync();
        }

        await using var ctx = NewInventoryDb(_household);
        var entries = await ctx.StockEntries
            .Where(e => e.ProductId == _productA
                     && e.LocationId == _locA
                     && e.DepletedAt == null)
            .ToListAsync();

        Assert.Equal(500m, entries.Sum(e => e.Quantity));
    }

    // ── ListNoLocationRows: active lots visible across all locations ───────────

    [Fact(DisplayName = "Active lots across multiple locations are all visible in the household context")]
    public async Task ActiveLots_VisibleAcrossLocations()
    {
        await using (var seedDb = NewInventoryDb(_household))
        {
            var stock = ProductStock.Start(_household, _productA, SystemClock.Instance);
            stock.AddStock(100m, _unitId, _locA, _userId, SystemClock.Instance);
            stock.AddStock(200m, _unitId, _locB, _userId, SystemClock.Instance);
            await seedDb.ProductStocks.AddAsync(stock);
            await seedDb.SaveChangesAsync();
        }

        await using var ctx = NewInventoryDb(_household);
        var loaded = await ctx.ProductStocks
            .Include(p => p.Entries)
            .SingleAsync(p => p.ProductId == _productA);

        var activeLots = loaded.ActiveLotsFefo().ToList();
        Assert.Equal(2, activeLots.Count);
        Assert.Equal(300m, activeLots.Sum(e => e.Quantity));
    }

    // ── RLS isolation: the location index path stays per-household ────────────

    [Fact(DisplayName = "RLS isolation: household A cannot read household B's stock via the location query")]
    public async Task RlsIsolation_LocationQuery_HouseholdACannotReadHouseholdB()
    {
        var householdB = HouseholdId.New();

        // Seed both households.
        await using (var seedA = NewInventoryDb(_household))
        {
            var stockA = ProductStock.Start(_household, _productA, SystemClock.Instance);
            stockA.AddStock(100m, _unitId, _locA, _userId, SystemClock.Instance);
            await seedA.ProductStocks.AddAsync(stockA);
            await seedA.SaveChangesAsync();
        }

        await using (var seedB = NewInventoryDb(householdB))
        {
            var stockB = ProductStock.Start(householdB, _productB, SystemClock.Instance);
            stockB.AddStock(999m, _unitId, _locA, _userId, SystemClock.Instance);
            await seedB.ProductStocks.AddAsync(stockB);
            await seedB.SaveChangesAsync();
        }

        // Household A context can only see household A's entries.
        await using var ctxA = NewInventoryDb(_household);
        var entries = await ctxA.StockEntries
            .Where(e => e.LocationId == _locA && e.DepletedAt == null)
            .ToListAsync();

        Assert.All(entries, e => Assert.Equal(_household, e.HouseholdId));
        Assert.DoesNotContain(entries, e => e.ProductId == _productB);
    }

    [Fact(DisplayName = "RLS backstop: raw SQL with app.household_id set returns only own location rows")]
    public async Task RlsBackstop_LocationQuery_OnlyOwnHousehold()
    {
        var householdB = HouseholdId.New();

        await using (var seedA = NewInventoryDb(_household))
        {
            var stockA = ProductStock.Start(_household, _productA, SystemClock.Instance);
            stockA.AddStock(100m, _unitId, _locA, _userId, SystemClock.Instance);
            await seedA.ProductStocks.AddAsync(stockA);
            await seedA.SaveChangesAsync();
        }

        await using (var seedB = NewInventoryDb(householdB))
        {
            var stockB = ProductStock.Start(householdB, _productB, SystemClock.Instance);
            stockB.AddStock(999m, _unitId, _locA, _userId, SystemClock.Instance);
            await seedB.ProductStocks.AddAsync(stockB);
            await seedB.SaveChangesAsync();
        }

        await using var conn = new NpgsqlConnection(db.AppUserConnectionString);
        await conn.OpenAsync();

        // Set app.household_id to household A.
        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = $"SET app.household_id = '{_household.Value}'";
            await setCmd.ExecuteNonQueryAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT household_id FROM inventory.stock_entry
            WHERE location_id = @loc AND depleted_at IS NULL
            """;
        cmd.Parameters.AddWithValue("loc", _locA);
        await using var reader = await cmd.ExecuteReaderAsync();

        var seen = new List<Guid>();
        while (await reader.ReadAsync())
            seen.Add(reader.GetGuid(0));

        Assert.NotEmpty(seen);
        Assert.All(seen, id => Assert.Equal(_household.Value, id));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private DbContextOptions<InventoryDbContext> Options() =>
        new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(db.ConnectionString).Options;

    private InventoryDbContext NewInventoryDb(HouseholdId household)
    {
        var ctx = new InventoryDbContext(Options());
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }

    /// <summary>Identity converter for consume calls in tests.</summary>
    private sealed class IdentityConverter : IQuantityConverter
    {
        public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId) => amount;
    }
}
