using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Plantry.Housekeeping.Domain;
using Plantry.Housekeeping.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Housekeeping;

/// <summary>
/// L3 integration tests proving Postgres RLS isolates the Housekeeping <c>dismissal</c> table exactly
/// like every other bounded context (tidy-up.md T9, ADR-008) — household A physically cannot read
/// household B's dismissals. Mirrors <c>StockRlsIsolationTests</c>.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class DismissalRlsIsolationTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _householdA;
    private HouseholdId _householdB;
    private readonly Guid _subjectA = Guid.CreateVersion7();
    private readonly Guid _subjectB = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _householdA = HouseholdId.New();
        _householdB = HouseholdId.New();

        await SeedHouseholdAsync(_householdA, _subjectA);
        await SeedHouseholdAsync(_householdB, _subjectB);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedHouseholdAsync(HouseholdId household, Guid subjectId)
    {
        await using var seedDb = NewHousekeepingDb(household);
        var dismissal = Dismissal.Create(
            household, DetectorId.StockUnitUnconvertible, subjectId, "fp-1", SystemClock.Instance);
        await seedDb.Dismissals.AddAsync(dismissal);
        await seedDb.SaveChangesAsync();
    }

    [Fact(DisplayName = "EF query filter: household A cannot see household B's dismissals")]
    public async Task EfFilter_HouseholdA_Cannot_Read_HouseholdB_Dismissals()
    {
        await using var housekeepingDb = NewHousekeepingDb(_householdA);

        var dismissals = await housekeepingDb.Dismissals.ToListAsync();

        Assert.All(dismissals, d => Assert.Equal(_householdA, d.HouseholdId));
        Assert.DoesNotContain(dismissals, d => d.SubjectId == _subjectB);
        var own = Assert.Single(dismissals);
        Assert.Equal(_subjectA, own.SubjectId);
    }

    [Fact(DisplayName = "Postgres RLS backstop: raw SQL with wrong app.household_id returns no dismissal rows")]
    public async Task RlsPolicy_RawSql_WithWrongHouseholdId_ReturnsNoRows()
    {
        await using var conn = new NpgsqlConnection(db.AppUserConnectionString);
        await conn.OpenAsync();

        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = $"SET app.household_id = '{_householdA.Value}'";
            await setCmd.ExecuteNonQueryAsync();
        }

        await using var selectCmd = conn.CreateCommand();
        selectCmd.CommandText = "SELECT household_id FROM housekeeping.dismissal";
        await using var reader = await selectCmd.ExecuteReaderAsync();

        var seenIds = new List<Guid>();
        while (await reader.ReadAsync())
            seenIds.Add(reader.GetGuid(0));

        Assert.NotEmpty(seenIds);
        Assert.All(seenIds, id => Assert.Equal(_householdA.Value, id));
        Assert.DoesNotContain(seenIds, id => id == _householdB.Value);
    }

    [Fact(DisplayName = "RLS backstop (live path): interceptor arms app.household_id; only own household's dismissals visible")]
    public async Task Interceptor_OnAppUserConnection_RlsRestrictsHousekeepingTablesToHousehold()
    {
        var tenant = new TenantContext();
        tenant.Set(_householdA.Value);

        var opts = BuildHousekeepingOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenant));
        await using var housekeepingDb = new HousekeepingDbContext(opts);

        var dismissals = await housekeepingDb.Dismissals.IgnoreQueryFilters().ToListAsync();

        Assert.NotEmpty(dismissals);
        Assert.All(dismissals, d => Assert.Equal(_householdA, d.HouseholdId));
        Assert.DoesNotContain(dismissals, d => d.SubjectId == _subjectB);
    }

    [Fact(DisplayName = "RLS backstop (live path): no tenant context => strict policy returns no dismissal rows")]
    public async Task Interceptor_NoTenantContext_StrictPolicy_ReturnsNoDismissalRows()
    {
        var tenant = new TenantContext(); // never set

        var opts = BuildHousekeepingOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenant));
        await using var housekeepingDb = new HousekeepingDbContext(opts);

        var dismissals = await housekeepingDb.Dismissals.IgnoreQueryFilters().ToListAsync();

        Assert.Empty(dismissals);
    }

    private DbContextOptions<HousekeepingDbContext> HousekeepingOptions() =>
        new DbContextOptionsBuilder<HousekeepingDbContext>().UseNpgsql(db.ConnectionString).Options;

    private static DbContextOptions<HousekeepingDbContext> BuildHousekeepingOptions(string connStr, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<HousekeepingDbContext>().UseNpgsql(connStr);
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return builder.Options;
    }

    private HousekeepingDbContext NewHousekeepingDb(HouseholdId household)
    {
        var ctx = new HousekeepingDbContext(HousekeepingOptions());
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }
}
