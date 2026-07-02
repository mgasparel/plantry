using Microsoft.Extensions.Options;

namespace Plantry.Web.Deals;

/// <summary>
/// The app's <b>first</b> <see cref="BackgroundService"/> (P5-6): drives <see cref="FlyerIngestionCycle"/>
/// on a <see cref="PeriodicTimer"/>, establishing the hosted-worker pattern (locked decision: in-process
/// in Plantry.Web, no new project — Aspire already runs the web app). A source/parse failure for any one
/// household or subscription is isolated downstream, so the timer loop itself only ever stops on
/// host shutdown.
/// <para>
/// The first tick waits a full <see cref="FlyerIngestionOptions.PollInterval"/> (24h default), so the
/// worker is inert during a short-lived test/E2E boot without any host-specific configuration; a manual
/// "pull now" trigger (dev, §7e) drives <see cref="FlyerIngestionCycle"/> on demand.
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
        using var timer = new PeriodicTimer(opts.PollInterval);

        // WaitForNextTickAsync waits one full interval before the first tick — the worker never fires at boot.
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await cycle.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // graceful shutdown
            }
            catch (Exception ex)
            {
                // A whole-cycle failure must not kill the worker — log and wait for the next tick.
                logger.LogError(ex, "Flyer ingestion cycle threw; the worker will retry next interval.");
            }
        }
    }
}
