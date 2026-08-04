using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Plantry.Market.Domain;
using Plantry.Market.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Market.Deals;

/// <summary>
/// L3 integration tests for <see cref="StoreSubscriptionRepository"/> (P5-2): the repository is
/// RLS-scoped, so a second household sees none of another's subscriptions (acceptance: "RLS"); and the
/// reactivation lookup (<c>FindByStore</c>) that a re-subscribe relies on resolves within-household only.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class StoreSubscriptionRepositoryTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly IClock Clock = SystemClock.Instance;

    private HouseholdId _householdA;
    private HouseholdId _householdB;
    private Guid _storeA;
    private Guid _storeB;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _householdA = HouseholdId.New();
        _householdB = HouseholdId.New();
        _storeA = Guid.NewGuid();
        _storeB = Guid.NewGuid();

        await using (var ctxA = NewRepoContext(_householdA))
            await new StoreSubscriptionRepository(ctxA).AddAndSaveAsync(
                StoreSubscription.Subscribe(_householdA, _storeA, "K1A0B1", Clock));

        await using (var ctxB = NewRepoContext(_householdB))
            await new StoreSubscriptionRepository(ctxB).AddAndSaveAsync(
                StoreSubscription.Subscribe(_householdB, _storeB, "M5V0A1", Clock));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "RLS: household A's repository lists only A's subscriptions, never B's")]
    public async Task List_Is_Household_Scoped()
    {
        await using var ctx = NewRepoContext(_householdA);
        var repo = new StoreSubscriptionRepository(ctx);

        var subs = await repo.ListAsync();

        var sub = Assert.Single(subs);
        Assert.Equal(_householdA, sub.HouseholdId);
        Assert.Equal(_storeA, sub.StoreId);
    }

    [Fact(DisplayName = "RLS: FindByStore resolves within-household only (B's store is invisible to A)")]
    public async Task FindByStore_Is_Household_Scoped()
    {
        await using var ctx = NewRepoContext(_householdA);
        var repo = new StoreSubscriptionRepository(ctx);

        Assert.NotNull(await repo.FindByStoreAsync(_storeA));
        Assert.Null(await repo.FindByStoreAsync(_storeB)); // B's store — not visible to A
    }

    [Fact(DisplayName = "Pause persists is_active=false without deleting the row (retained history)")]
    public async Task Pause_Persists_And_Retains_Row()
    {
        await using (var ctx = NewRepoContext(_householdA))
        {
            var repo = new StoreSubscriptionRepository(ctx);
            var sub = await repo.FindByStoreAsync(_storeA);
            sub!.Pause(Clock);
            await repo.SaveChangesAsync();
        }

        await using (var verify = NewRepoContext(_householdA))
        {
            var reloaded = await new StoreSubscriptionRepository(verify).FindByStoreAsync(_storeA);
            Assert.NotNull(reloaded);
            Assert.False(reloaded!.IsActive);
            Assert.Equal("K1A0B1", reloaded.PostalCode);
        }
    }

    // ── Cross-tenant boot due-check (plantry-rb36) ──────────────────────────────
    // FlyerIngestionWorker's boot due-check reads MAX(last_pulled_at) across every household with NO
    // tenant armed (mirrors HouseholdRepository.ListAllIdsAsync's carve-out — see RlsIsolationTests'
    // ListAllIds_NoTenant_ReturnsAllHouseholds_ScopedReturnsOwn for the Identity twin of this proof).
    // These run against the non-superuser app_user role with the real connection interceptor, so a
    // passing test proves the AllowCrossHouseholdStoreSubscriptionRead RLS carve-out actually fires —
    // not just the EF-layer IgnoreQueryFilters call.

    [Fact(DisplayName =
        "Cross-tenant boot due-check: no tenant sees the MAX last-pull across every household; a scoped tenant collapses to its own")]
    public async Task GetLastPulledAtAcrossHouseholds_NoTenant_ReturnsOverallMax_ScopedReturnsOwnMax()
    {
        var householdAPulledAt = new FixedClock(new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero));
        var householdBPulledAt = new FixedClock(householdAPulledAt.UtcNow + TimeSpan.FromHours(6)); // the overall max

        await using (var ctxA = NewRepoContext(_householdA))
        {
            var repo = new StoreSubscriptionRepository(ctxA);
            var sub = await repo.FindByStoreAsync(_storeA);
            sub!.RecordPull("flyer-a-1", householdAPulledAt);
            await repo.SaveChangesAsync();
        }

        await using (var ctxB = NewRepoContext(_householdB))
        {
            var repo = new StoreSubscriptionRepository(ctxB);
            var sub = await repo.FindByStoreAsync(_storeB);
            sub!.RecordPull("flyer-b-1", householdBPulledAt);
            await repo.SaveChangesAsync();
        }

        // No tenant armed — the worker's actual boot path — sees the overall max (household B's later pull).
        var noTenant = new TenantContext();
        await using (var unarmed = new MarketDbContext(
            BuildAppUserOptions(new HouseholdRlsConnectionInterceptor(noTenant))))
        {
            var max = await new StoreSubscriptionRepository(unarmed).GetLastPulledAtAcrossHouseholdsAsync();
            Assert.Equal(householdBPulledAt.UtcNow, max);
        }

        // Armed to household A (the misuse case) — RLS collapses the read to A's own max only, proving a
        // stray tenant-scoped call can never see another household's pull timestamp.
        var tenantA = new TenantContext();
        tenantA.Set(_householdA.Value);
        await using (var armed = new MarketDbContext(
            BuildAppUserOptions(new HouseholdRlsConnectionInterceptor(tenantA))))
        {
            var max = await new StoreSubscriptionRepository(armed).GetLastPulledAtAcrossHouseholdsAsync();
            Assert.Equal(householdAPulledAt.UtcNow, max);
        }
    }

    [Fact(DisplayName = "Cross-tenant boot due-check: returns null when no subscription has ever recorded a pull")]
    public async Task GetLastPulledAtAcrossHouseholds_NoPullsRecorded_ReturnsNull()
    {
        var noTenant = new TenantContext();
        await using var unarmed = new MarketDbContext(
            BuildAppUserOptions(new HouseholdRlsConnectionInterceptor(noTenant)));

        var max = await new StoreSubscriptionRepository(unarmed).GetLastPulledAtAcrossHouseholdsAsync();

        Assert.Null(max);
    }

    private MarketDbContext NewRepoContext(HouseholdId household)
    {
        var options = new DbContextOptionsBuilder<MarketDbContext>()
            .UseNpgsql(db.ConnectionString)
            .Options;
        var ctx = new MarketDbContext(options);
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }

    private DbContextOptions<MarketDbContext> BuildAppUserOptions(IInterceptor interceptor) =>
        new DbContextOptionsBuilder<MarketDbContext>()
            .UseNpgsql(db.AppUserConnectionString)
            .AddInterceptors(interceptor)
            .Options;

    /// <summary>A settable-time <see cref="IClock"/> so pull timestamps can be ordered deterministically.</summary>
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}

file static class RepoTestExtensions
{
    public static async Task AddAndSaveAsync(this StoreSubscriptionRepository repo, StoreSubscription sub)
    {
        await repo.AddAsync(sub);
        await repo.SaveChangesAsync();
    }
}
