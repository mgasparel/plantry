using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Plantry.SharedKernel;
using Plantry.Web.Background;
using Plantry.Web.Housekeeping;
using Xunit;

namespace Plantry.Tests.Web.Housekeeping;

/// <summary>
/// L2 unit tests for <see cref="TidyUpBadgeRefresher"/> — the T6 SWR single-flight guard (plantry-h0qq).
/// Uses a fake <see cref="IBackgroundTaskQueue"/> that never drains, so the in-flight guard set by the
/// first <see cref="TidyUpBadgeRefresher.RequestRefreshAsync"/> call is never released mid-test — exactly
/// the state needed to prove "at most one enqueued item while a refresh is pending" deterministically. The
/// work item's own tenancy-arming + <c>GetTidyUpPageQuery</c> delegation is covered separately at L4
/// (<c>TidyUpFragmentTests</c>) where a real DI scope is available.
/// </summary>
public sealed class TidyUpBadgeRefresherTests
{
    private static readonly HouseholdId HouseholdA = HouseholdId.New();
    private static readonly HouseholdId HouseholdB = HouseholdId.New();

    [Fact(DisplayName = "N concurrent requests for the same household — exactly one item enqueued (single-flight)")]
    public async Task ConcurrentRequests_SameHousehold_EnqueuesExactlyOnce()
    {
        var queue = new NonDrainingFakeQueue();
        var refresher = new TidyUpBadgeRefresher(queue, NullLogger<TidyUpBadgeRefresher>.Instance);

        var tasks = Enumerable.Range(0, 10).Select(_ => refresher.RequestRefreshAsync(HouseholdA));
        await Task.WhenAll(tasks);

        Assert.Equal(1, queue.EnqueueCount);
    }

    [Fact(DisplayName = "Different households — each gets its own enqueued item")]
    public async Task Requests_DifferentHouseholds_EnqueuesOnePerHousehold()
    {
        var queue = new NonDrainingFakeQueue();
        var refresher = new TidyUpBadgeRefresher(queue, NullLogger<TidyUpBadgeRefresher>.Instance);

        await refresher.RequestRefreshAsync(HouseholdA);
        await refresher.RequestRefreshAsync(HouseholdB);
        await refresher.RequestRefreshAsync(HouseholdA); // still in flight — no-op

        Assert.Equal(2, queue.EnqueueCount);
    }

    [Fact(DisplayName = "After the enqueued item runs (and even fails), a later request enqueues again (guard released)")]
    public async Task AfterItemRuns_LaterRequest_EnqueuesAgain()
    {
        // Drives the captured work item synchronously against an empty (unresolvable) service provider —
        // RefreshOneAsync throws resolving TenantContext, but the guard-release `finally` inside
        // TidyUpBadgeRefresher still runs regardless of that failure, and the outer catch swallows it (the
        // "never throws into the caller" contract). What this proves: the in-flight guard is released once
        // the item has actually run, not left permanently stuck.
        var queue = new SynchronousDrivingFakeQueue();
        var refresher = new TidyUpBadgeRefresher(queue, NullLogger<TidyUpBadgeRefresher>.Instance);

        await refresher.RequestRefreshAsync(HouseholdA);
        Assert.Equal(1, queue.EnqueueCount);

        await refresher.RequestRefreshAsync(HouseholdA); // guard was released — this enqueues a second item
        Assert.Equal(2, queue.EnqueueCount);
    }

    /// <summary>Invokes the work item immediately, synchronously, against an empty service provider.</summary>
    private sealed class SynchronousDrivingFakeQueue : IBackgroundTaskQueue
    {
        private static readonly IServiceProvider EmptyProvider = new ServiceCollection().BuildServiceProvider();
        public int EnqueueCount { get; private set; }

        public async ValueTask EnqueueAsync(
            Func<IServiceProvider, CancellationToken, Task> workItem, CancellationToken ct = default)
        {
            EnqueueCount++;
            try
            {
                await workItem(EmptyProvider, ct);
            }
            catch
            {
                // Expected — RefreshOneAsync can't resolve TenantContext from an empty provider. The point
                // of this fake is only to drive the work item's own finally-block guard release.
            }
        }

        public ValueTask<Func<IServiceProvider, CancellationToken, Task>> DequeueAsync(CancellationToken ct) =>
            throw new NotSupportedException("This fake drives items synchronously on enqueue.");
    }

    /// <summary>Records enqueue calls but never drains — the captured work item is simply held.</summary>
    private sealed class NonDrainingFakeQueue : IBackgroundTaskQueue
    {
        private int _enqueueCount;
        public int EnqueueCount => _enqueueCount;

        public ValueTask EnqueueAsync(
            Func<IServiceProvider, CancellationToken, Task> workItem, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _enqueueCount);
            return ValueTask.CompletedTask;
        }

        public ValueTask<Func<IServiceProvider, CancellationToken, Task>> DequeueAsync(CancellationToken ct) =>
            throw new NotSupportedException("This fake never drains.");
    }
}
