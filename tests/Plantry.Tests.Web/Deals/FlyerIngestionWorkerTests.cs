using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Plantry.Web.Deals;
using Xunit;

namespace Plantry.Tests.Web.Deals;

/// <summary>
/// Worker-level tests for FlyerIngestionWorker's orchestration (plantry-rb36 AC4/AC5), constructed
/// directly against a fake IFlyerIngestionCycle — no WAF/DI container needed, since the worker's own
/// dependencies (cycle, options, logger) are all directly injectable. The first BackgroundService
/// test in this codebase: exercises StartAsync/StopAsync and BackgroundService.ExecuteTask directly,
/// the standard .NET pattern for testing a hosted service without a full host.
/// </summary>
public sealed class FlyerIngestionWorkerTests
{
    private static FlyerIngestionWorker MakeWorker(FakeFlyerIngestionCycle cycle, FlyerIngestionOptions opts) =>
        new(cycle, Options.Create(opts), NullLogger<FlyerIngestionWorker>.Instance);

    [Fact(DisplayName = "AC4: Enabled=false short-circuits before any cross-household query or cycle run")]
    public async Task Disabled_NeverQueriesOrRuns()
    {
        var cycle = new FakeFlyerIngestionCycle();
        var worker = MakeWorker(cycle, new FlyerIngestionOptions { Enabled = false });

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)); // bounded: the disabled path returns in ~0ms, so a timeout here means the short-circuit regressed
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, cycle.GetLastPullCallCount);
        Assert.Equal(0, cycle.RunCallCount);
    }

    [Fact(DisplayName = "AC5: a boot-time last-pull query exception is caught; the worker still runs its first sweep and stays alive")]
    public async Task BootQueryException_IsCaught_WorkerStillSweepsAndKeepsRunning()
    {
        var cycle = new FakeFlyerIngestionCycle { GetLastPullThrows = new InvalidOperationException("boom") };
        var worker = MakeWorker(cycle, new FlyerIngestionOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromMilliseconds(30), // short so the test doesn't wait on a real 24h interval
        });

        await worker.StartAsync(CancellationToken.None);
        await PollUntilAsync(() => cycle.RunCallCount >= 1, "boot cycle never ran after the last-pull query threw");

        Assert.False(worker.ExecuteTask!.IsCompleted, "worker exited ExecuteAsync instead of entering the timer loop");

        await worker.StopAsync(CancellationToken.None);
        // Graceful shutdown leaves ExecuteTask Canceled (it was awaiting the PeriodicTimer tick when
        // StopAsync canceled stoppingToken) rather than RanToCompletion — that's the healthy outcome,
        // not a bug, so IsCompletedSuccessfully is the wrong assertion here. IsFaulted is a genuine but
        // secondary check: StopAsync cancels the stopping CTS and then awaits ExecuteTask with
        // ConfigureAwaitOptions.SuppressThrowing, so the task is always complete (and any fault already
        // observable) by the time StopAsync returns — this assertion is deterministic, not racy. It is
        // secondary only because the mutations under test kill the test earlier, at the PollUntilAsync
        // step above — the boot cycle running at all after the query threw.
        Assert.False(worker.ExecuteTask.IsFaulted, $"worker's ExecuteTask faulted instead of completing cleanly on shutdown: {worker.ExecuteTask.Exception}");
    }

    [Fact(DisplayName = "A cycle exception during the boot sweep is caught and the worker survives into the timer loop")]
    public async Task CycleException_DuringBootSweep_IsCaught_WorkerSurvivesToNextTick()
    {
        var cycle = new FakeFlyerIngestionCycle { RunThrowsOnCallNumber = 1 }; // the boot sweep itself throws
        var worker = MakeWorker(cycle, new FlyerIngestionOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromMilliseconds(30),
        });

        await worker.StartAsync(CancellationToken.None);
        // A second RunAsync call can only happen if the worker survived the first (throwing) one and
        // reached the PeriodicTimer loop for a subsequent tick.
        await PollUntilAsync(() => cycle.RunCallCount >= 2, "worker did not survive the boot cycle's exception into the timer loop");

        await worker.StopAsync(CancellationToken.None);
        // See BootQueryException_IsCaught_WorkerStillSweepsAndKeepsRunning above: graceful shutdown ends
        // the task Canceled, not RanToCompletion, and IsFaulted here is a genuine but secondary check
        // (deterministic, not racy — see that comment for why). It is secondary only because the actual
        // proof RunCycleSafelyAsync's catch block still works is the PollUntilAsync wait above — a
        // second RunAsync call only happens if the worker survived the first (throwing) one into the
        // timer loop.
        Assert.False(worker.ExecuteTask!.IsFaulted, $"worker's ExecuteTask faulted instead of completing cleanly on shutdown: {worker.ExecuteTask.Exception}");
    }

    /// <summary>Short real-time poll (not Task.Delay-then-check-once) so these tests don't flake under
    /// slow CI while still finishing in well under a second on the happy path. There's no TimeProvider
    /// seam on the worker's PeriodicTimer today (would need a new dependency + worker refactor to make
    /// deterministic) — out of scope for this ticket; the 2s deadline is generous enough that this
    /// should be as stable as any other timing-sensitive test in the suite.</summary>
    private static async Task PollUntilAsync(Func<bool> condition, string timeoutMessage)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition(), timeoutMessage);
    }
}

internal sealed class FakeFlyerIngestionCycle : IFlyerIngestionCycle
{
    public int GetLastPullCallCount { get; private set; }
    public int RunCallCount { get; private set; }
    public DateTimeOffset? LastPulledAtResult { get; set; }
    public Exception? GetLastPullThrows { get; set; }
    public int? RunThrowsOnCallNumber { get; set; }

    public Task<DateTimeOffset?> GetLastPullAcrossHouseholdsAsync(CancellationToken ct = default)
    {
        GetLastPullCallCount++;
        if (GetLastPullThrows is { } ex) throw ex;
        return Task.FromResult(LastPulledAtResult);
    }

    public Task RunAsync(CancellationToken ct = default)
    {
        RunCallCount++;
        if (RunThrowsOnCallNumber == RunCallCount)
            throw new InvalidOperationException($"fake cycle throw on call #{RunCallCount}");
        return Task.CompletedTask;
    }
}
