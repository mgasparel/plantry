using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Plantry.MealPlanning.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.MealPlanning;

/// <summary>
/// L3 integration tests proving Postgres RLS isolates the <c>meal_planning</c> tables — household A
/// physically cannot read household B's rows (ADR-008). Mirrors <c>RecipeRlsIsolationTests</c>.
/// <para>
/// Rows are seeded via the <see cref="MealPlanningReferenceDataSeeder"/> (which creates a MealSlotConfig
/// + three slots) because that is the domain factory path. MealPlan rows are inserted via raw SQL, as
/// there is no public constructor in this P3-0 skeleton slice.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class MealPlanningRlsIsolationTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _householdA;
    private HouseholdId _householdB;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _householdA = HouseholdId.New();
        _householdB = HouseholdId.New();

        // Seed a MealSlotConfig for each household via the reference-data seeder
        // (this also creates meal_slot rows, giving us rows to isolate).
        await SeedHouseholdAsync(_householdA);
        await SeedHouseholdAsync(_householdB);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedHouseholdAsync(HouseholdId household)
    {
        await using var seedDb = NewMealPlanningDb(household);
        var seeder = new MealPlanningReferenceDataSeeder(seedDb, SystemClock.Instance);
        await seeder.SeedAsync(household);
    }

    [Fact(DisplayName = "EF query filter: household A cannot see household B's meal slots")]
    public async Task EfFilter_HouseholdA_Cannot_Read_HouseholdB_Slots()
    {
        await using var mpDb = NewMealPlanningDb(_householdA);

        var slots = await mpDb.MealSlots.ToListAsync();

        Assert.All(slots, s => Assert.Equal(_householdA, s.HouseholdId));
        Assert.DoesNotContain(slots, s => s.HouseholdId == _householdB);
    }

    [Fact(DisplayName = "EF query filter: household A cannot see household B's meal slot configs")]
    public async Task EfFilter_HouseholdA_Cannot_Read_HouseholdB_Configs()
    {
        await using var mpDb = NewMealPlanningDb(_householdA);

        var configs = await mpDb.MealSlotConfigs.ToListAsync();

        Assert.All(configs, c => Assert.Equal(_householdA, c.HouseholdId));
        Assert.DoesNotContain(configs, c => c.HouseholdId == _householdB);
    }

    [Fact(DisplayName = "Postgres RLS backstop: raw SQL with wrong app.household_id returns no meal_slot rows")]
    public async Task RlsPolicy_RawSql_WithWrongHouseholdId_ReturnsNoMealSlotRows()
    {
        await using var conn = new NpgsqlConnection(db.AppUserConnectionString);
        await conn.OpenAsync();

        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = $"SET app.household_id = '{_householdA.Value}'";
            await setCmd.ExecuteNonQueryAsync();
        }

        await AssertOnlyHouseholdVisibleAsync(conn, "meal_planning.meal_slot", _householdA.Value, _householdB.Value);
    }

    [Fact(DisplayName = "RLS backstop (live path): interceptor arms app.household_id; only own household's slots visible")]
    public async Task Interceptor_OnAppUserConnection_RlsRestrictsSlots()
    {
        var tenant = new TenantContext();
        tenant.Set(_householdA.Value);

        var opts = BuildMealPlanningOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenant));
        await using var mpDb = new MealPlanningDbContext(opts);

        var slots = await mpDb.MealSlots.IgnoreQueryFilters().ToListAsync();

        Assert.NotEmpty(slots);
        Assert.All(slots, s => Assert.Equal(_householdA, s.HouseholdId));
        Assert.DoesNotContain(slots, s => s.HouseholdId == _householdB);
    }

    [Fact(DisplayName = "RLS backstop (live path): no tenant context => strict policy returns no meal_slot rows")]
    public async Task Interceptor_NoTenantContext_StrictPolicy_ReturnsNoMealSlotRows()
    {
        var tenant = new TenantContext(); // never set

        var opts = BuildMealPlanningOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenant));
        await using var mpDb = new MealPlanningDbContext(opts);

        var slots = await mpDb.MealSlots.IgnoreQueryFilters().ToListAsync();

        Assert.Empty(slots);
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

    private DbContextOptions<MealPlanningDbContext> MealPlanningOptions() =>
        new DbContextOptionsBuilder<MealPlanningDbContext>().UseNpgsql(db.ConnectionString).Options;

    private static DbContextOptions<MealPlanningDbContext> BuildMealPlanningOptions(string connStr, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<MealPlanningDbContext>().UseNpgsql(connStr);
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return builder.Options;
    }

    private MealPlanningDbContext NewMealPlanningDb(HouseholdId household)
    {
        var ctx = new MealPlanningDbContext(MealPlanningOptions());
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }
}
