using Microsoft.Extensions.Options;
using Plantry.SharedKernel.Domain;

namespace Plantry.Web.Deals;

/// <summary>
/// The app's <b>first</b> <see cref="BackgroundService"/> (P5-6): drives <see cref="FlyerIngestionCycle"/>
/// on a <see cref="PeriodicTimer"/>, establishing the hosted-worker pattern (locked decision: in-process
/// in Plantry.Web, no new project — Aspire already runs the web app). A source/parse failure for any one
/// household or subscription is isolated downstream, so the timer loop itself only ever stops on
/// host shutdown.
/// <para>
/// <b>Boot due-check (plantry-rb36).</b> A bare <see cref="PeriodicTimer"/> waits a full
/// <see cref="FlyerIngestionOptions.PollInterval"/> (24h default) before its first tick, no matter how
/// long it has actually been since the last sweep — so frequent redeploys (each restarting the process,
/// and with it the timer) can starve the sweep indefinitely even though flyers publish weekly. Before
/// entering the timer loop, <see cref="ExecuteAsync"/> now asks <see cref="FlyerIngestionCycle"/> for the
/// most recent successful pull across every household and uses <see cref="FlyerIngestionBootSchedule"/> to
/// decide: if a sweep is already due (none recorded, or a full interval has already elapsed), it runs
/// immediately; otherwise it waits only the remainder of the interval, so the first tick lands on
/// <c>lastPull + PollInterval</c> rather than restarting a fresh 24h clock from process start. This keeps
/// the "no sweep when none is due" guarantee — a short-lived test/E2E boot with a fresh database still sees
/// no *repeated* sweeping — while closing the starvation gap a bare timer left open.
/// </para>
/// </summary>
public sealed class FlyerIngestionWorker(
    FlyerIngestionCycle cycle,
    IOptions<FlyerIngestionOptions> options,
    ILogger<FlyerIngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            logger.LogInformation("Flyer ingestion worker disabled (Deals:Ingestion:Enabled=false).");
            return;
        }

        logger.LogInformation("Flyer ingestion worker started; interval {Interval}.", opts.PollInterval);

        if (!await RunBootDueCheckAsync(opts, stoppingToken))
            return; // host is shutting down — never reached the timer loop

        using var timer = new PeriodicTimer(opts.PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!await RunCycleSafelyAsync(stoppingToken))
                break; // graceful shutdown
        }
    }

    /// <summary>
    /// Decides — and, if due, performs — the worker's first sweep. Returns <c>false</c> only when host
    /// shutdown was observed before or during that first sweep (the caller must not enter the timer loop);
    /// <c>true</c> otherwise, whether or not a sweep actually ran.
    /// </summary>
    private async Task<bool> RunBootDueCheckAsync(FlyerIngestionOptions opts, CancellationToken stoppingToken)
    {
        DateTimeOffset? lastPulledAt;
        try
        {
            lastPulledAt = await cycle.GetLastPullAcrossHouseholdsAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            // Unknown last-pull state at boot: fail safe toward "due now" rather than risk silently
            // waiting a full interval on a query that may never have had data to begin with. The
            // subsequent RunCycleSafelyAsync call has its own isolation, so this can't crash the host.
            logger.LogError(ex, "Failed to read the cross-household last flyer pull at boot; treating a sweep as due.");
            lastPulledAt = null;
        }

        var now = SystemClock.Instance.UtcNow;
        var initialDelay = FlyerIngestionBootSchedule.ComputeInitialDelay(lastPulledAt, opts.PollInterval, now);

        if (initialDelay > TimeSpan.Zero)
        {
            logger.LogInformation(
                "Last flyer pull recorded at {LastPulledAt}; next sweep not due for {Delay}.",
                lastPulledAt, initialDelay);
            try
            {
                await Task.Delay(initialDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return false;
            }
        }
        else
        {
            logger.LogInformation("Flyer sweep is due at boot (last pull {LastPulledAt}); running immediately.", lastPulledAt);
        }

        return await RunCycleSafelyAsync(stoppingToken);
    }

    /// <summary>Runs one cycle, isolating a whole-cycle failure so the worker survives to retry next interval.
    /// Returns <c>false</c> only on a shutdown-triggered cancellation (the caller must stop looping).</summary>
    private async Task<bool> RunCycleSafelyAsync(CancellationToken stoppingToken)
    {
        try
        {
            await cycle.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Flyer ingestion cycle threw; the worker will retry next interval.");
        }

        return true;
    }
}
