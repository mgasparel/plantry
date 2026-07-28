namespace Plantry.Web.Deals;

/// <summary>
/// Pure boot due-check for <see cref="FlyerIngestionWorker"/> (plantry-rb36). Extracted as a static,
/// clock-parameterized function — no DI, no <c>IClock</c> — so the decision is unit-testable without a
/// host or a database. <see cref="FlyerIngestionWorker"/> supplies <paramref name="now"/> — see
/// <c>ComputeInitialDelay</c> below — from its injected singleton <see cref="TimeProvider"/> (plantry-hdry),
/// which sidesteps the Scoped-<c>IClock</c> captive-dependency problem a Singleton-hosted worker would
/// otherwise hit without needing any special-case workaround.
/// </summary>
internal static class FlyerIngestionBootSchedule
{
    /// <summary>
    /// How long the worker should wait before its first sweep, given the most recent successful pull
    /// recorded by <b>any</b> household (or <c>null</c> if none ever has).
    /// <see cref="TimeSpan.Zero"/> means "due now — run immediately": either no pull was ever recorded, or
    /// <paramref name="pollInterval"/> has already fully elapsed since <paramref name="lastPulledAt"/>.
    /// Otherwise the wait until exactly <c>lastPulledAt + pollInterval</c>, so the first tick lands on the
    /// locked daily cadence rather than restarting a fresh interval from "now" (the bug this fixes: a
    /// redeploy must never push the next sweep further into the future than it already was).
    /// </summary>
    public static TimeSpan ComputeInitialDelay(
        DateTimeOffset? lastPulledAt, TimeSpan pollInterval, DateTimeOffset now)
    {
        if (lastPulledAt is not { } last)
            return TimeSpan.Zero;

        var dueAt = last + pollInterval;
        return dueAt > now ? dueAt - now : TimeSpan.Zero;
    }
}
