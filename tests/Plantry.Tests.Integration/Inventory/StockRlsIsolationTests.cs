using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Plantry.Inventory.Domain;
using Plantry.Inventory.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Inventory;

/// <summary>
/// L3 integration tests proving Postgres RLS isolates the Inventory tables (<c>product_stock</c>,
/// <c>stock_entry</c>, <c>stock_journal_entry</c>) exactly like the Catalog tables — household A
/// physically cannot read household B's stock (PHASE-1-PLAN.md Slice 2, Stage B done-when:
/// "RLS isolation proven on the inventory schema"). Mirrors <c>ProductRlsIsolationTests</c>.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class StockRlsIsolationTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _householdA;
    private HouseholdId _householdB;
    private readonly Guid _productA = Guid.CreateVersion7();
    private readonly Guid _productB = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _householdA = HouseholdId.New();
        _householdB = HouseholdId.New();

        await SeedHouseholdAsync(_householdA, _productA);
        await SeedHouseholdAsync(_householdB, _productB);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedHouseholdAsync(HouseholdId household, Guid productId)
    {
        await using var seedDb = NewInventoryDb(household);
        var stock = ProductStock.Start(household, productId, SystemClock.Instance);
        stock.AddStock(100m, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            SystemClock.Instance, expiryDate: new DateOnly(2026, 7, 1));
        await seedDb.ProductStocks.AddAsync(stock);
        await seedDb.SaveChangesAsync();
    }

    [Fact(DisplayName = "EF query filter: household A cannot see household B's stock")]
    public async Task EfFilter_HouseholdA_Cannot_Read_HouseholdB_Stock()
    {
        await using var inventoryDb = NewInventoryDb(_householdA);

        var stocks = await inventoryDb.ProductStocks
            .Include(p => p.Entries)
            .Include(p => p.Journal)
            .ToListAsync();

        Assert.All(stocks, p => Assert.Equal(_householdA, p.HouseholdId));
        Assert.DoesNotContain(stocks, p => p.ProductId == _productB);

        var own = Assert.Single(stocks);
        Assert.All(own.Entries, e => Assert.Equal(_householdA, e.HouseholdId));
        Assert.All(own.Journal, j => Assert.Equal(_householdA, j.HouseholdId));
    }

    [Fact(DisplayName = "Postgres RLS backstop: raw SQL with wrong app.household_id returns no inventory rows")]
    public async Task RlsPolicy_RawSql_WithWrongHouseholdId_ReturnsNoRows()
    {
        // Connect as the non-superuser app_user role and prove the policies on all three inventory
        // tables fire (RLS never applies to superusers).
        await using var conn = new NpgsqlConnection(db.AppUserConnectionString);
        await conn.OpenAsync();

        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = $"SET app.household_id = '{_householdA.Value}'";
            await setCmd.ExecuteNonQueryAsync();
        }

        await AssertOnlyHouseholdVisibleAsync(conn, "inventory.product_stock", _householdA.Value, _householdB.Value);
        await AssertOnlyHouseholdVisibleAsync(conn, "inventory.stock_entry", _householdA.Value, _householdB.Value);
        await AssertOnlyHouseholdVisibleAsync(conn, "inventory.stock_journal_entry", _householdA.Value, _householdB.Value);
    }

    [Fact(DisplayName = "RLS backstop (live path): interceptor arms app.household_id; only own household's stock visible")]
    public async Task Interceptor_OnAppUserConnection_RlsRestrictsInventoryTablesToHousehold()
    {
        var tenant = new TenantContext();
        tenant.Set(_householdA.Value);

        var opts = BuildInventoryOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenant));
        await using var inventoryDb = new InventoryDbContext(opts);

        var stocks = await inventoryDb.ProductStocks
            .IgnoreQueryFilters()
            .Include(p => p.Entries)
            .ToListAsync();

        Assert.NotEmpty(stocks);
        Assert.All(stocks, p => Assert.Equal(_householdA, p.HouseholdId));
        Assert.DoesNotContain(stocks, p => p.ProductId == _productB);
    }

    [Fact(DisplayName = "RLS backstop (live path): no tenant context => strict policy returns no inventory rows")]
    public async Task Interceptor_NoTenantContext_StrictPolicy_ReturnsNoInventoryRows()
    {
        var tenant = new TenantContext(); // never set

        var opts = BuildInventoryOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenant));
        await using var inventoryDb = new InventoryDbContext(opts);

        var stocks = await inventoryDb.ProductStocks.IgnoreQueryFilters().ToListAsync();

        Assert.Empty(stocks);
    }

    private static async Task AssertOnlyHouseholdVisibleAsync(
        NpgsqlConnection conn, string table, Guid expectedHouseholdId, Guid forbiddenHouseholdId)
    {
        await using var selectCmd = conn.CreateCommand();
        selectCmd.CommandText = $"SELECT household_id FROM {table}";
        await using var reader = await selectCmd.ExecuteReaderAsync();

        var seenIds = new List<Guid>();
        while (await reader.ReadAsync())
            seenIds.Add(reader.GetGuid(0));

        Assert.NotEmpty(seenIds);
        Assert.All(seenIds, id => Assert.Equal(expectedHouseholdId, id));
        Assert.DoesNotContain(seenIds, id => id == forbiddenHouseholdId);
    }

    private DbContextOptions<InventoryDbContext> InventoryOptions() =>
        new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(db.ConnectionString).Options;

    private static DbContextOptions<InventoryDbContext> BuildInventoryOptions(string connStr, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(connStr);
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return builder.Options;
    }

    private InventoryDbContext NewInventoryDb(HouseholdId household)
    {
        var ctx = new InventoryDbContext(InventoryOptions());
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }
}
