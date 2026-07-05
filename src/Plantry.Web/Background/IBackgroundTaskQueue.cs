namespace Plantry.Web.Background;

/// <summary>
/// A minimal in-process fire-and-forget work queue (plantry-qll2.4). A request handler can enqueue a unit
/// of work that must run <b>after</b> the response — with no HTTP request or ambient DI scope alive — and
/// return immediately, so request latency is unaffected. <see cref="QueuedHostedService"/> drains the
/// queue on a single background loop, running each item in its <b>own</b> fresh DI scope.
///
/// <para>The work item is handed a fresh <see cref="IServiceProvider"/> (the per-item scope's provider):
/// it must resolve everything it needs from that scope and, for any tenant-scoped work, arm tenancy
/// itself (there is no ambient request), exactly as <c>FlyerIngestionCycle</c> does for the polling
/// worker. This queue is deliberately unpersisted — a host restart drops pending items. That is
/// acceptable for the conversion seed (a best-effort convenience that re-triggers on the next save of any
/// recipe with the same unit gap); do not use it for work that must survive a crash.</para>
/// </summary>
public interface IBackgroundTaskQueue
{
    /// <summary>
    /// Enqueues a work item. Non-blocking unless the bounded queue is full, in which case it awaits
    /// capacity. The item receives a fresh scope's <see cref="IServiceProvider"/> and a stopping token.
    /// </summary>
    ValueTask EnqueueAsync(Func<IServiceProvider, CancellationToken, Task> workItem, CancellationToken ct = default);

    /// <summary>Awaits and removes the next work item; completes when the host is shutting down.</summary>
    ValueTask<Func<IServiceProvider, CancellationToken, Task>> DequeueAsync(CancellationToken ct);
}
