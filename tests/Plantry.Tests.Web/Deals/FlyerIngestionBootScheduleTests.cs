using Plantry.Web.Deals;
using Xunit;

namespace Plantry.Tests.Web.Deals;

/// <summary>
/// Unit tests for the boot due-check (plantry-rb36) that fixed
/// <see cref="FlyerIngestionWorker"/>'s "never ticks at boot" starvation bug: a bare
/// <see cref="PeriodicTimer"/> always waited a full <c>PollInterval</c> before its first tick, so
/// frequent redeploys (each restarting the process and its timer) could starve the daily sweep
/// indefinitely. <see cref="FlyerIngestionBootSchedule.ComputeInitialDelay"/> is the pure decision the
/// worker now consults before entering the timer loop.
/// </summary>
public sealed class FlyerIngestionBootScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(24);

    [Fact(DisplayName = "AC1: no prior pull recorded => due immediately (Zero delay)")]
    public void NoPriorPull_IsDueImmediately()
    {
        var delay = FlyerIngestionBootSchedule.ComputeInitialDelay(
            lastPulledAt: null, pollInterval: PollInterval, now: Now);

        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact(DisplayName = "AC1: last pull older than PollInterval => due immediately (Zero delay)")]
    public void StaleLastPull_IsDueImmediately()
    {
        var lastPulledAt = Now - PollInterval - TimeSpan.FromHours(1); // 25h ago

        var delay = FlyerIngestionBootSchedule.ComputeInitialDelay(lastPulledAt, PollInterval, Now);

        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact(DisplayName = "AC1 boundary: last pull exactly PollInterval ago => due immediately (Zero delay)")]
    public void LastPullExactlyOneIntervalAgo_IsDueImmediately()
    {
        var lastPulledAt = Now - PollInterval;

        var delay = FlyerIngestionBootSchedule.ComputeInitialDelay(lastPulledAt, PollInterval, Now);

        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact(DisplayName = "AC2: recent pull => no immediate sweep; delay lands exactly on lastPull + PollInterval")]
    public void RecentLastPull_DelaysUntilExactlyDue()
    {
        var lastPulledAt = Now - TimeSpan.FromHours(20); // pulled 20h ago; 4h remain of the 24h interval

        var delay = FlyerIngestionBootSchedule.ComputeInitialDelay(lastPulledAt, PollInterval, Now);

        Assert.Equal(TimeSpan.FromHours(4), delay);
        Assert.Equal(lastPulledAt + PollInterval, Now + delay);
    }

    [Fact(DisplayName = "AC3: idempotent across a redeploy that recomputes with the same stored lastPull")]
    public void RedeployAtDifferentBootTimes_RecomputesConsistentDueTime_NoRepeatedSweep()
    {
        var lastPulledAt = Now - TimeSpan.FromHours(20);

        // First boot, right away.
        var firstDelay = FlyerIngestionBootSchedule.ComputeInitialDelay(lastPulledAt, PollInterval, Now);
        Assert.True(firstDelay > TimeSpan.Zero);

        // A redeploy an hour later recomputes from the SAME persisted lastPulledAt (no sweep happened
        // in between) — the due time it lands on must be identical, not a fresh interval from the new boot.
        var redeployNow = Now + TimeSpan.FromHours(1);
        var secondDelay = FlyerIngestionBootSchedule.ComputeInitialDelay(lastPulledAt, PollInterval, redeployNow);

        Assert.Equal(lastPulledAt + PollInterval, Now + firstDelay);
        Assert.Equal(lastPulledAt + PollInterval, redeployNow + secondDelay);
    }
}
