using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Aspire.Hosting.Testing;

namespace Plantry.Tests.E2E.Infrastructure;

/// <summary>
/// Boots the full Plantry service graph (Postgres + web app) from the Aspire AppHost
/// for the E2E run, so tests don't depend on an app instance started by hand.
/// </summary>
public sealed class AppHostFixture : IAsyncLifetime
{
    private DistributedApplication _app = null!;

    public string BaseUrl { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Plantry_AppHost>([
            "--environment=Testing"
        ]);
        _app = await appHost.BuildAsync();

        var resourceNotifications = _app.Services.GetRequiredService<ResourceNotificationService>();
        await _app.StartAsync();
        await resourceNotifications
            .WaitForResourceAsync("plantry-web", KnownResourceStates.Running)
            .WaitAsync(TimeSpan.FromMinutes(2));

        BaseUrl = _app.GetEndpoint("plantry-web").ToString().TrimEnd('/');

        // Poll until the web server is actually serving requests, not just "Running" at the
        // process level. Under full-suite load (integration tests competing for Docker),
        // the process can enter Running state while still completing DB migrations.
        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var r = await http.GetAsync("/Account/Login");
                if ((int)r.StatusCode < 500) break;
            }
            catch { }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();
}

[CollectionDefinition(nameof(AppHostCollection))]
public sealed class AppHostCollection : ICollectionFixture<AppHostFixture>;
