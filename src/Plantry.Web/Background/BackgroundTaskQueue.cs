using System.Threading.Channels;

namespace Plantry.Web.Background;

/// <summary>
/// Bounded <see cref="Channel{T}"/>-backed <see cref="IBackgroundTaskQueue"/> (plantry-qll2.4). The bound
/// caps how many post-response work items can be in flight so a burst of saves cannot grow memory without
/// limit; <see cref="BoundedChannelFullMode.Wait"/> back-pressures the (rare) producer rather than
/// dropping work. Registered as a singleton so producers (request handlers) and the single consumer
/// (<see cref="QueuedHostedService"/>) share one channel.
/// </summary>
public sealed class BackgroundTaskQueue : IBackgroundTaskQueue
{
    private readonly Channel<Func<IServiceProvider, CancellationToken, Task>> _queue;

    public BackgroundTaskQueue(int capacity = 256)
    {
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        };
        _queue = Channel.CreateBounded<Func<IServiceProvider, CancellationToken, Task>>(options);
    }

    public async ValueTask EnqueueAsync(
        Func<IServiceProvider, CancellationToken, Task> workItem, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        await _queue.Writer.WriteAsync(workItem, ct);
    }

    public async ValueTask<Func<IServiceProvider, CancellationToken, Task>> DequeueAsync(CancellationToken ct) =>
        await _queue.Reader.ReadAsync(ct);
}
