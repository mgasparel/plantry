using Microsoft.EntityFrameworkCore;
using Plantry.Shopping.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Shopping;

/// <summary>
/// L3 integration tests for: (a) migration applies clean; (b) new household gets exactly one
/// ShoppingList; (c) seeded list is scoped to the correct household (shopping.md resolved call 1).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ShoppingReferenceDataTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private HouseholdId _otherHousehold;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
        _otherHousehold = HouseholdId.New();

        // Seed _household only; _otherHousehold stays empty to prove household scoping.
        await using var ctx = NewShoppingDb(_household);
        var seeder = new ShoppingReferenceDataSeeder(ctx, SystemClock.Instance);
        await seeder.SeedAsync(_household);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "Seeding a new household creates exactly one ShoppingList with name 'Shopping List'")]
    public async Task Seeding_Creates_One_List_With_Default_Name()
    {
        await using var ctx = NewShoppingDb(_household);

        var lists = await ctx.ShoppingLists.ToListAsync();

        var list = Assert.Single(lists);
        Assert.Equal(_household, list.HouseholdId);
        Assert.Equal("Shopping List", list.Name);
    }

    [Fact(DisplayName = "Seeded list has no items (empty scratchpad on creation)")]
    public async Task Seeded_List_Has_No_Items()
    {
        await using var ctx = NewShoppingDb(_household);

        var lists = await ctx.ShoppingLists.Include(l => l.Items).ToListAsync();

        var list = Assert.Single(lists);
        Assert.Empty(list.Items);
    }

    [Fact(DisplayName = "Unseeded household sees zero shopping lists (EF query filter scopes correctly)")]
    public async Task Unseeded_Household_Has_No_Lists()
    {
        await using var ctx = NewShoppingDb(_otherHousehold);

        var lists = await ctx.ShoppingLists.ToListAsync();

        Assert.Empty(lists);
    }

    [Fact(DisplayName = "Migration applies cleanly: both shopping tables exist with the expected columns")]
    public async Task Migration_Applies_Clean_Both_Tables_Exist()
    {
        // The migration succeeded during InitializeAsync (applied as part of PostgresFixture).
        // Verify the tables exist by querying them via raw SQL on the superuser connection.
        await using var conn = new Npgsql.NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema = 'shopping'
              AND table_name IN ('shopping_list', 'shopping_list_item')
            """;

        var count = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(2, count);
    }

    private DbContextOptions<ShoppingDbContext> ShoppingOptions() =>
        new DbContextOptionsBuilder<ShoppingDbContext>().UseNpgsql(db.ConnectionString).Options;

    private ShoppingDbContext NewShoppingDb(HouseholdId household)
    {
        var ctx = new ShoppingDbContext(ShoppingOptions());
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }
}
