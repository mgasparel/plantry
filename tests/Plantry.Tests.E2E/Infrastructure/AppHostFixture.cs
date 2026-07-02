using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Plantry.Tests.E2E.Infrastructure;

/// <summary>
/// Boots the full Plantry service graph (Postgres + web app) from the Aspire AppHost
/// for the E2E run, so tests don't depend on an app instance started by hand.
/// </summary>
public sealed class AppHostFixture : IAsyncLifetime
{
    private DistributedApplication _app = null!;

    public string BaseUrl { get; private set; } = null!;

    /// <summary>
    /// Owner connection string to the application's Postgres database. Used by E2E tests that assert
    /// side effects with no UI surface (e.g. price observations written during intake commit). As the
    /// database owner this connection is not subject to RLS, so a test can read any household's rows.
    /// </summary>
    public string DbConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Plantry_AppHost>([
            "--environment=Testing"
        ]);

        // The AppHost declares a required `postgres-password` parameter (pinned so local-dev re-runs
        // keep a stable password matching the persistent data volume). The E2E run uses an ephemeral
        // container (volume stripped below), so the value is irrelevant — but the parameter must still
        // resolve. Supply a fixed test value so neither CI nor a contributor running E2E locally needs
        // a user-secret.
        appHost.Configuration["Parameters:postgres-password"] = "e2e-test-password";

        // Remove the named data volume from postgres so each E2E run gets a fresh ephemeral container
        // with no data persisted between runs.
        var postgresResource = appHost.Resources.OfType<PostgresServerResource>().Single();
        foreach (var mount in postgresResource.Annotations.OfType<ContainerMountAnnotation>().ToList())
            postgresResource.Annotations.Remove(mount);

        // Swap the real Gemini receipt parser for a deterministic, no-network, no-API-key fake on the
        // web process — so the intake E2E journey (ReceiptIntakeJourneyTests) never hits a live AI call
        // and CI needs no OpenRouter secret. This honours the AI:UseFakeParser seam in Program.cs and is
        // confined to the test AppHost; production always wires the real parser. (Env key uses '__' as the
        // .NET configuration section delimiter for "AI:UseFakeParser".)
        var webResource = appHost.Resources
            .OfType<ProjectResource>()
            .Single(r => r.Name == "plantry-web");
        appHost.CreateResourceBuilder(webResource)
            .WithEnvironment("AI__UseSampleParser", "false")
            .WithEnvironment("AI__UseFakeParser", "true")
            // Keep the deterministic canned StubFlyerSourceAdapter for the Stores & Deals journey
            // (StoresAndDealsJourneyTests) so no live Flipp call is made in CI. Honours the
            // Deals:UseStubFlyerSource seam in Program.cs; confined to the test AppHost (production wires
            // the real Flipp FlyerSource). ('__' is the .NET config section delimiter for "Deals:UseStubFlyerSource".)
            .WithEnvironment("Deals__UseStubFlyerSource", "true");

        _app = await appHost.BuildAsync();

        var resourceNotifications = _app.Services.GetRequiredService<ResourceNotificationService>();
        await _app.StartAsync();
        await resourceNotifications
            .WaitForResourceAsync("plantry-web", KnownResourceStates.Running)
            .WaitAsync(TimeSpan.FromMinutes(2));

        BaseUrl = _app.GetEndpoint("plantry-web").ToString().TrimEnd('/');
        DbConnectionString = await _app.GetConnectionStringAsync("plantrydb")
            ?? throw new InvalidOperationException("plantrydb connection string was not available from the AppHost.");

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
