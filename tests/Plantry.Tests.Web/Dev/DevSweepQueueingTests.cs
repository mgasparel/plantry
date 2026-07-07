using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Web.Background;
using Xunit;

namespace Plantry.Tests.Web.Dev;

/// <summary>
/// Guards the async contract of the three long-running dev sweeps (plantry-a2t8): each must queue its
/// cycle onto the shared <see cref="IBackgroundTaskQueue"/> and return <c>202 Accepted</c> immediately,
/// rather than awaiting the cycle inline on the request thread bound to <c>HttpContext.RequestAborted</c>
/// (where a client timeout/disconnect would silently cancel the sweep mid-run). We replace the queue with a
/// recording fake, POST each endpoint, and assert on the 202 + a single enqueue — proving the request path
/// no longer runs the sweep and never blocks on it. A regression to the old inline <c>await cycle.RunAsync</c>
/// fails here: it would resolve the real cycle (touching the placeholder DB) instead of enqueuing, so the
/// endpoint would neither return 202 nor record an enqueue.
/// </summary>
public sealed class DevSweepQueueingTests
{
    [Theory]
    [InlineData("/Dev/Deals/PullNow")]
    [InlineData("/Dev/Pricing/BackfillPurchaseStores")]
    [InlineData("/Dev/Recipes/BackfillConversions")]
    public async Task Endpoint_Queues_The_Sweep_And_Returns_202(string path)
    {
        await using var factory = new QueueRecordingDevFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var before = factory.Queue.EnqueuedCount;
        var response = await client.PostAsync(path, content: null);

        // Returns 202 immediately — the sweep runs later under the host lifetime token, not the request.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        // Exactly one work item was enqueued for this trigger (the sweep was NOT awaited inline).
        Assert.Equal(before + 1, factory.Queue.EnqueuedCount);
    }
}

/// <summary>
/// Boots Plantry.Web in Development (so the dev sweep endpoints are mapped) with the
/// <see cref="IBackgroundTaskQueue"/> replaced by a recording fake. The fake never yields a work item, so
/// <c>QueuedHostedService</c> parks and the (DB-touching) cycles never run — no Postgres is required.
/// </summary>
file sealed class QueueRecordingDevFactory : WebApplicationFactory<Program>
{
    public RecordingBackgroundTaskQueue Queue { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:plantrydb"] =
                    "Host=127.0.0.1;Port=9;Database=plantrydb;Username=app_user;Password=x;Timeout=1;CommandTimeout=1",
                ["DataProtection:KeyPath"] = Path.GetTempPath(),
            });
        });
        builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IBackgroundTaskQueue>(Queue)));
    }
}

/// <summary>Records enqueues; parks on dequeue until host shutdown so no work item ever executes.</summary>
file sealed class RecordingBackgroundTaskQueue : IBackgroundTaskQueue
{
    private int _enqueued;

    public int EnqueuedCount => Volatile.Read(ref _enqueued);

    public ValueTask EnqueueAsync(
        Func<IServiceProvider, CancellationToken, Task> workItem, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        Interlocked.Increment(ref _enqueued);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<Func<IServiceProvider, CancellationToken, Task>> DequeueAsync(CancellationToken ct)
    {
        // Park until the host stops; QueuedHostedService treats the resulting cancellation as graceful shutdown.
        await Task.Delay(Timeout.Infinite, ct);
        throw new OperationCanceledException(ct); // unreachable — Delay throws on cancellation.
    }
}
