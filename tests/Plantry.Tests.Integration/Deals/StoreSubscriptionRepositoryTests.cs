using Microsoft.EntityFrameworkCore;
using Plantry.Deals.Domain;
using Plantry.Deals.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Deals;

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

    private DealsDbContext NewRepoContext(HouseholdId household)
    {
        var options = new DbContextOptionsBuilder<DealsDbContext>()
            .UseNpgsql(db.ConnectionString)
            .Options;
        var ctx = new DealsDbContext(options);
        ctx.SetHouseholdId(household.Value);
        return ctx;
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
