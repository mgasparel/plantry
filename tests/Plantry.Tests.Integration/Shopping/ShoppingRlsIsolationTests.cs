using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Plantry.Shopping.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Shopping;

/// <summary>
/// L3 integration tests proving Postgres RLS isolates the Shopping tables — household A physically
/// cannot read household B's shopping rows (shopping.md RLS pattern, ADR-008/ADR-010).
/// Mirrors RecipeRlsIsolationTests.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ShoppingRlsIsolationTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _householdA;
    private HouseholdId _householdB;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _householdA = HouseholdId.New();
        _householdB = HouseholdId.New();

        // Seed one list per household using the seeder (the production path).
        var clock = SystemClock.Instance;

        await using (var dbA = NewShoppingDb(_householdA))
        {
            var seeder = new ShoppingReferenceDataSeeder(dbA, clock);
            await seeder.SeedAsync(_householdA);
        }

        await using (var dbB = NewShoppingDb(_householdB))
        {
            var seeder = new ShoppingReferenceDataSeeder(dbB, clock);
            await seeder.SeedAsync(_householdB);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "EF query filter: household A cannot see household B's shopping list")]
    public async Task EfFilter_HouseholdA_Cannot_Read_HouseholdB_ShoppingList()
    {
        await using var ctx = NewShoppingDb(_householdA);

        var lists = await ctx.ShoppingLists.ToListAsync();

        Assert.All(lists, l => Assert.Equal(_householdA, l.HouseholdId));
        Assert.DoesNotContain(lists, l => l.HouseholdId == _householdB);
    }

    [Fact(DisplayName = "Postgres RLS backstop: raw SQL with wrong app.household_id returns no shopping rows")]
    public async Task RlsPolicy_RawSql_WithWrongHouseholdId_ReturnsNoRows()
    {
        await using var conn = new NpgsqlConnection(db.AppUserConnectionString);
        await conn.OpenAsync();

        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = $"SET app.household_id = '{_householdA.Value}'";
            await setCmd.ExecuteNonQueryAsync();
        }

        await AssertOnlyHouseholdVisibleAsync(conn, "shopping.shopping_list", _householdA.Value, _householdB.Value);
    }

    [Fact(DisplayName = "RLS backstop (live path): interceptor arms app.household_id; only own household's list visible")]
    public async Task Interceptor_OnAppUserConnection_RlsRestrictsShoppingListToHousehold()
    {
        var tenant = new TenantContext();
        tenant.Set(_householdA.Value);

        var opts = BuildShoppingOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenant));
        await using var ctx = new ShoppingDbContext(opts);

        var lists = await ctx.ShoppingLists.IgnoreQueryFilters().ToListAsync();

        Assert.NotEmpty(lists);
        Assert.All(lists, l => Assert.Equal(_householdA, l.HouseholdId));
        Assert.DoesNotContain(lists, l => l.HouseholdId == _householdB);
    }

    [Fact(DisplayName = "RLS backstop (live path): no tenant context => strict policy returns no shopping rows")]
    public async Task Interceptor_NoTenantContext_StrictPolicy_ReturnsNoShoppingRows()
    {
        var tenant = new TenantContext(); // never set

        var opts = BuildShoppingOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenant));
        await using var ctx = new ShoppingDbContext(opts);

        var lists = await ctx.ShoppingLists.IgnoreQueryFilters().ToListAsync();

        Assert.Empty(lists);
    }

    [Fact(DisplayName = "Shopping list items are RLS-isolated: household A items invisible to household B")]
    public async Task RlsPolicy_Items_AreIsolated_By_Household()
    {
        // Seed an item under household A
        await using (var ctx = NewShoppingDb(_householdA))
        {
            var list = await ctx.ShoppingLists.FirstAsync();
            var clock = SystemClock.Instance;
            list.AddItem(Guid.CreateVersion7(), null, null, null, Plantry.Shopping.Domain.ItemSource.Manual, null, clock);
            await ctx.SaveChangesAsync();
        }

        // Household B sees no items
        await using var conn = new NpgsqlConnection(db.AppUserConnectionString);
        await conn.OpenAsync();

        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = $"SET app.household_id = '{_householdB.Value}'";
            await setCmd.ExecuteNonQueryAsync();
        }

        await using var selectCmd = conn.CreateCommand();
        selectCmd.CommandText = "SELECT COUNT(*) FROM shopping.shopping_list_item";
        var count = (long)(await selectCmd.ExecuteScalarAsync())!;

        Assert.Equal(0, count);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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

    private DbContextOptions<ShoppingDbContext> ShoppingOptions() =>
        new DbContextOptionsBuilder<ShoppingDbContext>().UseNpgsql(db.ConnectionString).Options;

    private static DbContextOptions<ShoppingDbContext> BuildShoppingOptions(
        string connStr, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<ShoppingDbContext>().UseNpgsql(connStr);
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return builder.Options;
    }

    private ShoppingDbContext NewShoppingDb(HouseholdId household)
    {
        var ctx = new ShoppingDbContext(ShoppingOptions());
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }
}
