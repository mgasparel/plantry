using System.Threading.Channels;

namespace Plantry.Web.Background;

/// <summary>
/// Bounded <see cref="Channel{T}"/>-backed <see cref="IBackgroundTaskQueue"/> (plantry-qll2.4). The bound
/// caps how many post-response work items can be in flight so a burst of saves cannot grow memory without
/// limit. <see cref="BoundedChannelFullMode.DropWrite"/> silently drops a new work item when the queue is
/// saturated rather than back-pressuring the producer (plantry-23mb): the sole payload is the best-effort,
/// idempotent recipe-conversion seed, which is derivable state — a dropped seed re-triggers on the next
/// save of any recipe with the same unit gap and self-heals opportunistically on cook — so the channel is a
/// latency optimization, not a work-of-record queue. DropWrite keeps <see cref="EnqueueAsync"/> non-blocking
/// unconditionally, honouring qll2.4's 'fire-and-forget post-save; the save never waits' guarantee even when
/// the 256-item queue is full. Registered as a singleton so producers (request handlers) and the single
/// consumer (<see cref="QueuedHostedService"/>) share one channel.
/// </summary>
public sealed class BackgroundTaskQueue : IBackgroundTaskQueue
{
    private readonly Channel<Func<IServiceProvider, CancellationToken, Task>> _queue;

    public BackgroundTaskQueue(int capacity = 256)
    {
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
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
