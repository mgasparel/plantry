using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Plantry.Deals.Domain;
using Plantry.Deals.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Deals;

/// <summary>
/// L3 integration tests proving the <c>deals</c> migration applies clean and enforces its contract:
/// per-household RLS isolation (ADR-008), the within-context composite FK
/// <c>deal → flyer_import (household_id, flyer_import_id)</c> (RESTRICT), and the three uniques.
/// The <see cref="PostgresFixture"/> applies every migration on container start, so a broken Deals
/// migration fails this collection's <c>InitializeAsync</c> outright.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class DealsSchemaTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly IClock Clock = SystemClock.Instance;

    private HouseholdId _householdA;
    private HouseholdId _householdB;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _householdA = HouseholdId.New();
        _householdB = HouseholdId.New();

        await SeedSubscriptionAsync(_householdA, Guid.NewGuid());
        await SeedSubscriptionAsync(_householdB, Guid.NewGuid());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static ValidityWindow Window() =>
        ValidityWindow.Create(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 7)).Value;

    private async Task SeedSubscriptionAsync(HouseholdId household, Guid storeId)
    {
        await using var ctx = NewDealsDb(household);
        ctx.StoreSubscriptions.Add(StoreSubscription.Subscribe(household, storeId, "K1A0B1", Clock));
        await ctx.SaveChangesAsync();
    }

    [Fact(DisplayName = "EF query filter: household A cannot see household B's subscriptions")]
    public async Task EfFilter_HouseholdA_Cannot_Read_HouseholdB_Subscriptions()
    {
        await using var ctx = NewDealsDb(_householdA);

        var subs = await ctx.StoreSubscriptions.ToListAsync();

        Assert.NotEmpty(subs);
        Assert.All(subs, s => Assert.Equal(_householdA, s.HouseholdId));
        Assert.DoesNotContain(subs, s => s.HouseholdId == _householdB);
    }

    [Fact(DisplayName = "Postgres RLS backstop: raw SQL with the wrong app.household_id returns no rows")]
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
        selectCmd.CommandText = "SELECT household_id FROM deals.store_subscription";
        await using var reader = await selectCmd.ExecuteReaderAsync();

        var seen = new List<Guid>();
        while (await reader.ReadAsync()) seen.Add(reader.GetGuid(0));

        Assert.NotEmpty(seen);
        Assert.All(seen, id => Assert.Equal(_householdA.Value, id));
        Assert.DoesNotContain(seen, id => id == _householdB.Value);
    }

    [Fact(DisplayName = "RLS backstop (live path): no tenant context => strict policy returns no rows")]
    public async Task Interceptor_NoTenantContext_StrictPolicy_ReturnsNoRows()
    {
        var tenant = new TenantContext(); // never set

        var opts = BuildOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenant));
        await using var ctx = new DealsDbContext(opts);

        var subs = await ctx.StoreSubscriptions.IgnoreQueryFilters().ToListAsync();

        Assert.Empty(subs);
    }

    [Fact(DisplayName = "Unique (household_id, store_id): a duplicate subscription is rejected (DD9)")]
    public async Task Unique_Subscription_PerHouseholdStore()
    {
        var storeId = Guid.NewGuid();
        await SeedSubscriptionAsync(_householdA, storeId);

        await using var ctx = NewDealsDb(_householdA);
        ctx.StoreSubscriptions.Add(StoreSubscription.Subscribe(_householdA, storeId, "K1A0B1", Clock));

        await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }

    [Fact(DisplayName = "Composite FK: a deal referencing a non-existent flyer_import is rejected (RESTRICT)")]
    public async Task CompositeFk_Deal_RequiresFlyerImport()
    {
        var storeId = Guid.NewGuid();
        await using var ctx = NewDealsDb(_householdA);

        // A staged deal pointing at a flyer_import id that was never inserted must violate the FK.
        var orphan = Deal.Stage(
            _householdA, FlyerImportId.New(), storeId,
            new RawDeal("Milk", null, null, 2.99m, null, null, null, Window()),
            DealNormalizer.Normalize("Milk"), MatchProposal.Unmatched(), Clock);
        ctx.Deals.Add(orphan);

        await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }

    [Fact(DisplayName = "Composite FK holds: a deal linked to an existing flyer_import persists")]
    public async Task CompositeFk_Deal_WithFlyerImport_Persists()
    {
        var storeId = Guid.NewGuid();
        await using var ctx = NewDealsDb(_householdA);

        var import = FlyerImport.Start(_householdA, storeId, "flyer-x", null, Window(), "{}", Clock);
        ctx.FlyerImports.Add(import);
        await ctx.SaveChangesAsync();

        var deal = Deal.Stage(
            _householdA, import.Id, storeId,
            new RawDeal("Milk 2L", "Dairyland", "2L", 3.49m, 2m, null, "Save $1", Window()),
            DealNormalizer.Normalize("Milk 2L"), new MatchProposal(null, MatchConfidence.None, null), Clock);
        ctx.Deals.Add(deal);
        await ctx.SaveChangesAsync();

        var persisted = await ctx.Deals.SingleAsync(d => d.Id == deal.Id);
        Assert.Equal(import.Id, persisted.FlyerImportId);
        Assert.Equal("milk", persisted.NormalizedName);
        Assert.Equal(DealStatus.Pending, persisted.Status);
    }

    [Fact(DisplayName = "Nullable composite FK: a manual-path deal (flyer_import_id null) persists")]
    public async Task CompositeFk_NullFlyerImport_Persists()
    {
        var storeId = Guid.NewGuid();
        await using var ctx = NewDealsDb(_householdA);

        var manual = Deal.Stage(
            _householdA, flyerImportId: null, storeId,
            new RawDeal("Bananas", null, null, 0.59m, null, null, null, Window()),
            DealNormalizer.Normalize("Bananas"), MatchProposal.Unmatched(), Clock);
        ctx.Deals.Add(manual);

        await ctx.SaveChangesAsync();

        var persisted = await ctx.Deals.SingleAsync(d => d.Id == manual.Id);
        Assert.Null(persisted.FlyerImportId);
    }

    private DbContextOptions<DealsDbContext> Options() =>
        new DbContextOptionsBuilder<DealsDbContext>().UseNpgsql(db.ConnectionString).Options;

    private static DbContextOptions<DealsDbContext> BuildOptions(string connStr, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<DealsDbContext>().UseNpgsql(connStr);
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return builder.Options;
    }

    private DealsDbContext NewDealsDb(HouseholdId household)
    {
        var ctx = new DealsDbContext(Options());
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }
}
