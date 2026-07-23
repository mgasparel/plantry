using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Identity.Domain;
using Plantry.SharedKernel;
using Plantry.Web.Background;
using Plantry.Web.Housekeeping;
using Xunit;

namespace Plantry.Tests.Web.Housekeeping;

/// <summary>
/// L2 unit tests for <see cref="TidyUpBadgeWarmup"/> — the T6 startup population sweep (plantry-h0qq):
/// enumerates every household via <see cref="IHouseholdRepository.ListAllIdsAsync"/> and requests one
/// background refresh per household. Uses a real <see cref="TidyUpBadgeRefresher"/> over a
/// non-draining fake queue (mirrors <c>TidyUpBadgeRefresherTests</c>) so "requested a refresh" can be
/// observed as "enqueued an item" without needing a full DI scope for the recompute itself.
/// </summary>
public sealed class TidyUpBadgeWarmupTests
{
    [Fact(DisplayName = "Enumerates every household and requests one refresh per household")]
    public async Task RunAsync_RequestsOneRefreshPerHousehold()
    {
        var households = new[] { HouseholdId.New(), HouseholdId.New(), HouseholdId.New() };
        var scopeFactory = BuildScopeFactory(new FakeHouseholdRepository(households));
        var queue = new CountingFakeQueue();
        var refresher = new TidyUpBadgeRefresher(queue, NullLogger<TidyUpBadgeRefresher>.Instance);
        var warmup = new TidyUpBadgeWarmup(scopeFactory, refresher, NullLogger<TidyUpBadgeWarmup>.Instance);

        await warmup.RunAsync();

        Assert.Equal(households.Length, queue.EnqueueCount);
    }

    [Fact(DisplayName = "No households — no refresh requested, and RunAsync completes without error")]
    public async Task RunAsync_NoHouseholds_RequestsNothing()
    {
        var scopeFactory = BuildScopeFactory(new FakeHouseholdRepository([]));
        var queue = new CountingFakeQueue();
        var refresher = new TidyUpBadgeRefresher(queue, NullLogger<TidyUpBadgeRefresher>.Instance);
        var warmup = new TidyUpBadgeWarmup(scopeFactory, refresher, NullLogger<TidyUpBadgeWarmup>.Instance);

        await warmup.RunAsync();

        Assert.Equal(0, queue.EnqueueCount);
    }

    [Fact(DisplayName = "Household enumeration throws — RunAsync swallows the failure instead of propagating it")]
    public async Task RunAsync_EnumerationThrows_DoesNotPropagate()
    {
        var services = new ServiceCollection();
        services.AddScoped<IHouseholdRepository>(_ => new ThrowingHouseholdRepository());
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var queue = new CountingFakeQueue();
        var refresher = new TidyUpBadgeRefresher(queue, NullLogger<TidyUpBadgeRefresher>.Instance);
        var warmup = new TidyUpBadgeWarmup(scopeFactory, refresher, NullLogger<TidyUpBadgeWarmup>.Instance);

        // Must not throw — a warmup failure must never affect app startup (see TidyUpBadgeWarmup.RunAsync).
        await warmup.RunAsync();

        Assert.Equal(0, queue.EnqueueCount);
    }

    private static IServiceScopeFactory BuildScopeFactory(IHouseholdRepository repo)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => repo);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class FakeHouseholdRepository(IReadOnlyList<HouseholdId> households) : IHouseholdRepository
    {
        public Task<Household?> FindAsync(HouseholdId id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<HouseholdId>> ListAllIdsAsync(CancellationToken ct = default) =>
            Task.FromResult(households);

        public Task AddAsync(Household household, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ThrowingHouseholdRepository : IHouseholdRepository
    {
        public Task<Household?> FindAsync(HouseholdId id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<HouseholdId>> ListAllIdsAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated database failure during warmup.");

        public Task AddAsync(Household household, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>Records enqueue calls but never drains — mirrors <c>TidyUpBadgeRefresherTests</c>'s double.</summary>
    private sealed class CountingFakeQueue : IBackgroundTaskQueue
    {
        public int EnqueueCount { get; private set; }

        public ValueTask EnqueueAsync(
            Func<IServiceProvider, CancellationToken, Task> workItem, CancellationToken ct = default)
        {
            EnqueueCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<Func<IServiceProvider, CancellationToken, Task>> DequeueAsync(CancellationToken ct) =>
            throw new NotSupportedException("This fake never drains.");
    }
}
