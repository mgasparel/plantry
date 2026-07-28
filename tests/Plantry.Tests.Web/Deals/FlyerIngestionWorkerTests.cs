using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Plantry.Web.Deals;
using Xunit;

namespace Plantry.Tests.Web.Deals;

/// <summary>
/// Worker-level tests for FlyerIngestionWorker's orchestration (plantry-rb36 AC4/AC5), constructed
/// directly against a fake IFlyerIngestionCycle — no WAF/DI container needed, since the worker's own
/// dependencies (cycle, options, logger, TimeProvider) are all directly injectable. The first
/// BackgroundService test in this codebase: exercises StartAsync/StopAsync and BackgroundService.ExecuteTask
/// directly, the standard .NET pattern for testing a hosted service without a full host.
/// <para>
/// <b>Determinism (plantry-hdry, Gate 10A).</b> The worker's boot "now" read, its <c>Delay</c>, and the
/// <see cref="PeriodicTimer"/> are all driven by an injected <see cref="TimeProvider"/>, so these tests
/// substitute <see cref="SignallingFakeTimeProvider"/> and advance it explicitly instead of sleeping real
/// wall-clock time. Synchronization with the worker's background execution never busy-polls
/// <c>DateTime.UtcNow</c>; it goes through two awaited signals instead:
/// <see cref="FakeFlyerIngestionCycle.WaitForRunCallAsync"/> (the fake completes a per-call-number
/// <see cref="TaskCompletionSource"/> as each <c>RunAsync</c> call happens) and
/// <see cref="SignallingFakeTimeProvider.WaitForTimerCreatedAsync"/> (completes once the worker has
/// registered a timer — the <see cref="PeriodicTimer"/> or the boot <c>Delay</c> — against the fake clock).
/// The latter matters because <c>Advance()</c> only affects timers already registered: calling it before the
/// worker reaches its <c>CreateTimer</c> call loses the tick permanently instead of merely racing.
/// </para>
/// <para>
/// Note: since .NET 10, <c>BackgroundService.ExecuteAsync</c> runs entirely on a background thread (including
/// its synchronous prefix — see the "BackgroundService runs all of ExecuteAsync as a Task" breaking change),
/// so nothing about the worker's boot sequence is observable synchronously right after <c>StartAsync</c>
/// returns; every assertion here goes through an awaited signal instead.
/// </para>
/// </summary>
public sealed class FlyerIngestionWorkerTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private static FlyerIngestionWorker MakeWorker(
        FakeFlyerIngestionCycle cycle, FlyerIngestionOptions opts, TimeProvider timeProvider) =>
        new(cycle, Options.Create(opts), NullLogger<FlyerIngestionWorker>.Instance, timeProvider);

    [Fact(DisplayName = "AC4: Enabled=false short-circuits before any cross-household query or cycle run")]
    public async Task Disabled_NeverQueriesOrRuns()
    {
        var cycle = new FakeFlyerIngestionCycle();
        var worker = MakeWorker(cycle, new FlyerIngestionOptions { Enabled = false }, new FakeTimeProvider());

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!.WaitAsync(WaitTimeout); // bounded: the disabled path returns in ~0ms, so a timeout here means the short-circuit regressed
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, cycle.GetLastPullCallCount);
        Assert.Equal(0, cycle.RunCallCount);
    }

    [Fact(DisplayName = "AC5: a boot-time last-pull query exception is caught; the worker still runs its first sweep and stays alive")]
    public async Task BootQueryException_IsCaught_WorkerStillSweepsAndKeepsRunning()
    {
        var pollInterval = TimeSpan.FromMilliseconds(30);
        var timeProvider = new SignallingFakeTimeProvider();
        var cycle = new FakeFlyerIngestionCycle { GetLastPullThrows = new InvalidOperationException("boom") };
        var worker = MakeWorker(cycle, new FlyerIngestionOptions
        {
            Enabled = true,
            PollInterval = pollInterval,
        }, timeProvider);

        await worker.StartAsync(CancellationToken.None);
        await cycle.WaitForRunCallAsync(1).WaitAsync(WaitTimeout); // boot cycle ran despite the last-pull query throwing

        // "…stays alive" is only proven by surviving into a SECOND cycle run — reachable exclusively from
        // the PeriodicTimer loop, not merely from ExecuteTask not being complete yet (which would pass even
        // if the timer loop were deleted, since the boot sweep alone leaves ExecuteTask incomplete too).
        await timeProvider.WaitForTimerCreatedAsync(1).WaitAsync(WaitTimeout); // PeriodicTimer registered — advancing earlier loses the tick
        timeProvider.Advance(pollInterval);
        // A second RunAsync call is only reachable from the PeriodicTimer loop — proof the worker entered it.
        await cycle.WaitForRunCallAsync(2).WaitAsync(WaitTimeout);
        Assert.False(worker.ExecuteTask!.IsCompleted, "worker exited ExecuteAsync instead of staying in the timer loop");

        await worker.StopAsync(CancellationToken.None);
        // Graceful shutdown leaves ExecuteTask Canceled (it was awaiting the PeriodicTimer tick when
        // StopAsync canceled stoppingToken) rather than RanToCompletion — that's the healthy outcome,
        // not a bug, so IsCompletedSuccessfully is the wrong assertion here. IsFaulted is a genuine but
        // secondary check: StopAsync cancels the stopping CTS and then awaits ExecuteTask with
        // ConfigureAwaitOptions.SuppressThrowing, so the task is always complete (and any fault already
        // observable) by the time StopAsync returns — this assertion is deterministic, not racy. It is
        // secondary only because the mutations under test kill the test earlier, at the WaitForRunCallAsync(2)
        // await above — the worker surviving into a second cycle at all after the boot query threw.
        Assert.False(worker.ExecuteTask.IsFaulted, $"worker's ExecuteTask faulted instead of completing cleanly on shutdown: {worker.ExecuteTask.Exception}");
    }

    [Fact(DisplayName = "A cycle exception during the boot sweep is caught and the worker survives into the timer loop")]
    public async Task CycleException_DuringBootSweep_IsCaught_WorkerSurvivesToNextTick()
    {
        var pollInterval = TimeSpan.FromMilliseconds(30);
        var timeProvider = new SignallingFakeTimeProvider();
        var cycle = new FakeFlyerIngestionCycle { RunThrowsOnCallNumber = 1 }; // the boot sweep itself throws
        var worker = MakeWorker(cycle, new FlyerIngestionOptions
        {
            Enabled = true,
            PollInterval = pollInterval,
        }, timeProvider);

        await worker.StartAsync(CancellationToken.None);
        // Wait for the (throwing) boot sweep, then for the worker to actually register the PeriodicTimer's
        // first-tick wait against the fake clock, before advancing — Advance() only affects timers already
        // registered, so calling it any earlier would lose the tick rather than merely race for it.
        await cycle.WaitForRunCallAsync(1).WaitAsync(WaitTimeout);
        await timeProvider.WaitForTimerCreatedAsync(1).WaitAsync(WaitTimeout); // PeriodicTimer registered — advancing earlier loses the tick
        timeProvider.Advance(pollInterval);
        // A second RunAsync call can only happen if the worker survived the first (throwing) one and
        // reached the PeriodicTimer loop for a subsequent tick.
        await cycle.WaitForRunCallAsync(2).WaitAsync(WaitTimeout);

        await worker.StopAsync(CancellationToken.None);
        // See BootQueryException_IsCaught_WorkerStillSweepsAndKeepsRunning above: graceful shutdown ends
        // the task Canceled, not RanToCompletion, and IsFaulted here is a genuine but secondary check
        // (deterministic, not racy — see that comment for why). It is secondary only because the actual
        // proof RunCycleSafelyAsync's catch block still works is the WaitForRunCallAsync(2) await above —
        // a second RunAsync call only happens if the worker survived the first (throwing) one into the timer loop.
        Assert.False(worker.ExecuteTask!.IsFaulted, $"worker's ExecuteTask faulted instead of completing cleanly on shutdown: {worker.ExecuteTask.Exception}");
    }

    [Fact(DisplayName = "The boot due-check's remaining-interval wait is driven by the injected TimeProvider, not the wall clock")]
    public async Task BootDelay_RunsOnFakeTime_WhenASweepIsNotYetDue()
    {
        var pollInterval = TimeSpan.FromHours(24);
        var timeProvider = new SignallingFakeTimeProvider();
        var cycle = new FakeFlyerIngestionCycle
        {
            // 18h since the last pull: 6h of the 24h interval still to wait, keyed off the SAME clock the
            // worker itself reads for "now" — this is the exact seam plantry-hdry closed (the boot due-check
            // used to read the real wall clock for "now" while waiting on the injected TimeProvider, which
            // would silently disagree with a fake clock and skip this delay path entirely).
            LastPulledAtResult = timeProvider.GetUtcNow() - TimeSpan.FromHours(18),
        };
        var worker = MakeWorker(cycle, new FlyerIngestionOptions { Enabled = true, PollInterval = pollInterval }, timeProvider);

        await worker.StartAsync(CancellationToken.None);
        await timeProvider.WaitForTimerCreatedAsync(1).WaitAsync(WaitTimeout); // the boot Delay registered with the fake clock
        // No sweep may have run yet: the timer registered above must be the boot Delay, not the
        // PeriodicTimer that follows a sweep. This is what pins the "now" half of the seam — if the
        // due-check read the ambient wall clock, the 2000-epoch fake lastPulledAt would look ancient,
        // the delay would collapse to zero, and the boot sweep would already have run by this point.
        Assert.Equal(0, cycle.RunCallCount);
        timeProvider.Advance(TimeSpan.FromHours(6));
        // Only reachable if the boot wait ran on the injected TimeProvider: on the real wall clock a 6h
        // delay never elapses within this test's bounded timeout.
        await cycle.WaitForRunCallAsync(1).WaitAsync(WaitTimeout);

        await worker.StopAsync(CancellationToken.None);
        Assert.False(worker.ExecuteTask!.IsFaulted, $"worker's ExecuteTask faulted: {worker.ExecuteTask.Exception}");
    }
}

/// <summary>FakeTimeProvider that also signals when a timer has been registered against it. Both
/// <c>PeriodicTimer(TimeSpan, TimeProvider)</c> and <c>Task.Delay(TimeSpan, TimeProvider, CancellationToken)</c>
/// call <see cref="CreateTimer"/> synchronously as part of starting the wait, so awaiting
/// <see cref="WaitForTimerCreatedAsync"/> is the deterministic "the worker is now waiting on fake time"
/// barrier — without it, <c>Advance()</c> can land before registration and the tick is lost forever (not
/// merely delayed), because <c>Advance()</c> only fires callbacks already registered.</summary>
internal sealed class SignallingFakeTimeProvider : FakeTimeProvider
{
    private readonly object _gate = new();
    private readonly List<(int Count, TaskCompletionSource Signal)> _pending = [];
    private int _timersCreated;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        var count = Interlocked.Increment(ref _timersCreated);
        List<TaskCompletionSource> due;
        lock (_gate)
        {
            due = _pending.Where(p => p.Count <= count).Select(p => p.Signal).ToList();
            _pending.RemoveAll(p => p.Count <= count);
        }
        foreach (var tcs in due)
            tcs.TrySetResult();
        return timer;
    }

    /// <summary>Returns a task that completes once <see cref="CreateTimer"/> has been called at least
    /// <paramref name="count"/> times.</summary>
    public Task WaitForTimerCreatedAsync(int count)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _timersCreated) >= count)
                return Task.CompletedTask;
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add((count, tcs));
            return tcs.Task;
        }
    }
}

internal sealed class FakeFlyerIngestionCycle : IFlyerIngestionCycle
{
    private readonly object _gate = new();
    private readonly List<(int CallNumber, TaskCompletionSource Signal)> _pendingRunSignals = [];
    private int _getLastPullCallCount;
    private int _runCallCount;

    // Interlocked-backed (plantry-hdry): the worker invokes these on a background/thread-pool thread while
    // the test thread reads them, and the wall-clock busy-poll that used to incidentally serialize access
    // (Task.Delay(10) between reads) is gone now that tests synchronize via WaitForRunCallAsync instead — an
    // unsynchronized read here would be a real torn-read risk, not just a theoretical one.
    public int GetLastPullCallCount => Volatile.Read(ref _getLastPullCallCount);
    public int RunCallCount => Volatile.Read(ref _runCallCount);

    public DateTimeOffset? LastPulledAtResult { get; set; }
    public Exception? GetLastPullThrows { get; set; }
    public int? RunThrowsOnCallNumber { get; set; }

    public Task<DateTimeOffset?> GetLastPullAcrossHouseholdsAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _getLastPullCallCount);
        if (GetLastPullThrows is { } ex) throw ex;
        return Task.FromResult(LastPulledAtResult);
    }

    public Task RunAsync(CancellationToken ct = default)
    {
        var callNumber = Interlocked.Increment(ref _runCallCount);
        SignalCallersWaitingUpTo(callNumber);
        if (RunThrowsOnCallNumber == callNumber)
            throw new InvalidOperationException($"fake cycle throw on call #{callNumber}");
        return Task.CompletedTask;
    }

    /// <summary>Returns a task that completes once <see cref="RunAsync"/> has been called at least
    /// <paramref name="callNumber"/> times — unconditionally, even for a call that goes on to throw, so a
    /// caller can deterministically await "the Nth call happened" instead of busy-polling
    /// <see cref="RunCallCount"/> against the wall clock.</summary>
    public Task WaitForRunCallAsync(int callNumber)
    {
        lock (_gate)
        {
            if (RunCallCount >= callNumber)
                return Task.CompletedTask;
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRunSignals.Add((callNumber, tcs));
            return tcs.Task;
        }
    }

    private void SignalCallersWaitingUpTo(int callNumber)
    {
        List<TaskCompletionSource>? due = null;
        lock (_gate)
        {
            if (_pendingRunSignals.Count == 0)
                return;
            due = _pendingRunSignals.Where(p => p.CallNumber <= callNumber).Select(p => p.Signal).ToList();
            _pendingRunSignals.RemoveAll(p => p.CallNumber <= callNumber);
        }
        foreach (var tcs in due)
            tcs.TrySetResult();
    }
}
