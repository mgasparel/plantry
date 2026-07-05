using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Plantry.Web.Dev;
using Xunit;

namespace Plantry.Tests.Web.Dev;

/// <summary>
/// Covers the dev-only /Dev/Endpoints reference page (plantry-bgg7):
///   1. The <see cref="DevEndpointRegistry"/> is the single source of truth and orders by path.
///   2. <c>MapDevPost</c> records into the registry — so a new endpoint appears with no page edit.
///   3. In Development the page renders every registered endpoint (method + path + description +
///      an htmx invoke button), with the destructive /Dev/Reset flagged and confirm-guarded.
///   4. Outside Development the page 404s via DevPagesGateMiddleware, like all /Dev paths.
/// </summary>
public sealed class DevEndpointsPageTests
{
    // ── Registry ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Registry_Orders_Endpoints_By_Path_Case_Insensitively()
    {
        var registry = new DevEndpointRegistry();
        registry.Add(new DevEndpoint("POST", "/Dev/Reset", "wipe", Destructive: true));
        registry.Add(new DevEndpoint("POST", "/Dev/Seed", "seed", Destructive: false));
        registry.Add(new DevEndpoint("POST", "/Dev/Deals/PullNow", "pull", Destructive: false));

        var paths = registry.Endpoints.Select(e => e.Path).ToArray();

        Assert.Equal(new[] { "/Dev/Deals/PullNow", "/Dev/Reset", "/Dev/Seed" }, paths);
    }

    // ── MapDevPost contract (acceptance #5: added via the helper → appears, no page edit) ──

    [Fact]
    public void MapDevPost_Records_The_Endpoint_Into_The_Registry()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<DevEndpointRegistry>();
        var app = builder.Build();

        app.MapDevPost("/Dev/NewThing", () => Results.Ok(), "does a brand new thing", destructive: true);

        var registry = app.Services.GetRequiredService<DevEndpointRegistry>();
        var entry = Assert.Single(registry.Endpoints);
        Assert.Equal("POST", entry.Method);
        Assert.Equal("/Dev/NewThing", entry.Path);
        Assert.Equal("does a brand new thing", entry.Description);
        Assert.True(entry.Destructive);
    }

    // ── Rendered page (Development) ───────────────────────────────────────────────────

    private static readonly (string Path, string DescriptionFragment, bool Destructive)[] ExpectedEndpoints =
    [
        ("/Dev/Seed", "seed fake demo data", false),
        ("/Dev/Reset", "Wipe ALL data", true),
        ("/Dev/Deals/PullNow", "flyer-ingestion sweep", false),
        ("/Dev/Pricing/BackfillPurchaseStores", "purchase-store-id backfill", false),
        ("/Dev/Recipes/BackfillConversions", "AI-suggested conversions", false),
    ];

    [Fact]
    public async Task Development_Page_Renders_All_Endpoints_With_Method_Path_Description_And_Invoke()
    {
        await using var factory = new DevEnvironmentFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = true });

        var response = await client.GetAsync("/Dev/Endpoints");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        var doc = new HtmlParser().ParseDocument(html);

        var rows = doc.QuerySelectorAll("li.dev-endpoint");
        Assert.Equal(ExpectedEndpoints.Length, rows.Length);

        foreach (var (path, descriptionFragment, destructive) in ExpectedEndpoints)
        {
            var button = doc.QuerySelector($"button[hx-post=\"{path}\"]");
            Assert.NotNull(button); // invoke button POSTs to the endpoint via htmx (acceptance #2)

            var row = button!.Closest("li.dev-endpoint");
            Assert.NotNull(row);

            // Method + path + one-line description all render on the row (acceptance #1).
            Assert.Contains("POST", row!.QuerySelector(".dev-endpoint__method")!.TextContent);
            Assert.Equal(path, row.QuerySelector(".dev-endpoint__path")!.TextContent.Trim());
            Assert.Contains(descriptionFragment, row.QuerySelector(".dev-endpoint__desc")!.TextContent,
                StringComparison.OrdinalIgnoreCase);

            // Inline status target + in-flight disable (acceptance #2).
            Assert.NotNull(row.QuerySelector(".dev-endpoint__status"));
            Assert.Equal("this", button.GetAttribute("hx-disabled-elt"));

            if (destructive)
            {
                // /Dev/Reset renders as destructive and confirm-guarded (acceptance #3).
                Assert.Contains("dev-endpoint--danger", row.ClassName ?? "");
                Assert.NotNull(row.QuerySelector(".badge--danger"));
                Assert.False(string.IsNullOrWhiteSpace(button.GetAttribute("hx-confirm")));
            }
            else
            {
                Assert.Null(button.GetAttribute("hx-confirm"));
            }
        }
    }

    // ── Gating (acceptance #4) ────────────────────────────────────────────────────────

    [Fact]
    public async Task Outside_Development_The_Page_Is_404()
    {
        await using var factory = new TestingEnvironmentFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Dev/Endpoints");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

/// <summary>
/// Boots Plantry.Web in the <b>Development</b> environment so the dev endpoints are mapped (and thus
/// registered) and /Dev/Endpoints renders. The page reads only the in-memory registry, so no database
/// is required; a placeholder connection string keeps DI construction happy.
/// </summary>
file sealed class DevEnvironmentFactory : WebApplicationFactory<Program>
{
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
    }
}

/// <summary>Boots in the non-Development "Testing" env — DevPagesGateMiddleware must 404 all /Dev paths.</summary>
file sealed class TestingEnvironmentFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:plantrydb"] =
                    "Host=127.0.0.1;Port=9;Database=plantrydb;Username=app_user;Password=x;Timeout=1;CommandTimeout=1",
                ["DataProtection:KeyPath"] = Path.GetTempPath(),
            });
        });
    }
}
