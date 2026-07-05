namespace Plantry.Web.Background;

/// <summary>
/// Drains <see cref="IBackgroundTaskQueue"/> on a single background loop (plantry-qll2.4), running each
/// work item in its <b>own</b> fresh DI scope so scoped services (DbContexts, the tenant context) are
/// isolated per item and disposed after it. A single failing item is caught and logged so it never kills
/// the loop — the next item still runs, and the loop itself only stops on host shutdown. Mirrors the
/// isolation discipline of <c>FlyerIngestionWorker</c>, the app's other <see cref="BackgroundService"/>.
/// </summary>
public sealed class QueuedHostedService(
    IBackgroundTaskQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<QueuedHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Background task queue worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            Func<IServiceProvider, CancellationToken, Task> workItem;
            try
            {
                workItem = await queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // graceful shutdown
            }

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await workItem(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // graceful shutdown mid-item
            }
            catch (Exception ex)
            {
                // A single work item's failure must not stop the drain loop — log and continue.
                logger.LogError(ex, "Background work item threw; the queue worker will continue.");
            }
        }
    }
}
