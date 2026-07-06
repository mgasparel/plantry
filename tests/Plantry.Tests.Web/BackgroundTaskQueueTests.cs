using Plantry.Web.Background;

namespace Plantry.Tests.Web;

/// <summary>
/// Guards the non-blocking post-save contract of <see cref="BackgroundTaskQueue"/> (plantry-23mb). With
/// <see cref="System.Threading.Channels.BoundedChannelFullMode.DropWrite"/>, enqueuing onto a saturated
/// queue must complete synchronously (dropping the new item) rather than back-pressuring the awaited
/// producer in the post-save request path. These tests lock DropWrite in so a future revert to Wait —
/// which would reintroduce the blocking enqueue qll2.4's 'the save never waits' guarantee forbids — fails.
/// </summary>
public sealed class BackgroundTaskQueueTests
{
    private static Func<IServiceProvider, CancellationToken, Task> NoopWork() =>
        static (_, _) => Task.CompletedTask;

    [Fact]
    public async Task EnqueueAsync_WhenQueueSaturated_CompletesWithoutBlocking()
    {
        // Capacity 1, and never dequeue: the second write would block forever under FullMode.Wait.
        var queue = new BackgroundTaskQueue(capacity: 1);

        await queue.EnqueueAsync(NoopWork());

        // The second enqueue must return (dropping the item) rather than awaiting drain capacity. Guard
        // with a timeout so a regression to Wait fails deterministically instead of hanging the suite.
        var second = queue.EnqueueAsync(NoopWork()).AsTask();
        var completed = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(second, completed);
        await second; // observe no exception
    }

    [Fact]
    public async Task EnqueueAsync_DropsWrite_KeepingOnlyItemsWithinCapacity()
    {
        var queue = new BackgroundTaskQueue(capacity: 1);

        // Item that flips a flag when run, then a second item that flips a different flag.
        var firstRan = false;
        var secondRan = false;
        await queue.EnqueueAsync((_, _) => { firstRan = true; return Task.CompletedTask; });
        await queue.EnqueueAsync((_, _) => { secondRan = true; return Task.CompletedTask; }); // dropped

        // Only one item is buffered; draining it yields the first. A second dequeue would have nothing.
        var drained = await queue.DequeueAsync(CancellationToken.None);
        await drained(null!, CancellationToken.None);

        Assert.True(firstRan);
        Assert.False(secondRan);
    }

    [Fact]
    public async Task EnqueueAsync_NullWorkItem_Throws()
    {
        var queue = new BackgroundTaskQueue();
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await queue.EnqueueAsync(null!));
    }
}
