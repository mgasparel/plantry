using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Plantry.Tests.Web;

/// <summary>
/// Asserts the /ready DB readiness probe security contract and unhealthy-state HTTP semantics
/// against the full Kestrel/Razor pipeline via WebApplicationFactory.
///
/// Unhealthy state is exercised by pointing the DbContext connection string at an unreachable
/// host — the real <c>AddDbContextCheck&lt;PlantryIdentityDbContext&gt;</c> from Program.cs
/// then returns Unhealthy, exercising the production code path end-to-end with no mocking.
///
/// The healthy state (200 "Healthy" when a real Postgres is up) is covered by the E2E suite
/// (Plantry.Tests.E2E.ReadinessEndpointTests), which boots the full Aspire stack with a live
/// database. This split avoids spinning up a real DB in the L4 Web test suite.
///
/// Security contract: /ready must NEVER emit check names, durations, or exception text.
/// This is what makes public production exposure safe (unlike /health, which stays dev-only).
/// </summary>
public sealed class ReadinessEndpointTests
{
    // ── Unhealthy state (DB unreachable) ──────────────────────────────────────────────────────

    [Fact(DisplayName = "/ready returns 503 Unhealthy when DB is unreachable")]
    public async Task Ready_Returns_503_Unhealthy_When_DB_Is_Unreachable()
    {
        await using var factory = new DeadDbFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("Unhealthy", body.Trim());
    }

    // ── Security contract: no detail in the body (gate from the design spec) ─────────────────

    [Fact(DisplayName = "/ready body contains no check name, duration, or exception detail when unhealthy")]
    public async Task Ready_Unhealthy_Body_Contains_No_Check_Detail()
    {
        await using var factory = new DeadDbFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/ready");
        var body = await response.Content.ReadAsStringAsync();

        // Must not leak check name ("db"), timing, or exception / error text
        // — public exposure safety contract from the design spec Security section.
        Assert.DoesNotContain("db", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("duration", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connection", body, StringComparison.OrdinalIgnoreCase);
        // The only permitted content is the status string.
        Assert.Equal("Unhealthy", body.Trim());
    }

    // ── Liveness (/alive) is unaffected by a DB connectivity failure ─────────────────────────

    [Fact(DisplayName = "/alive still returns 200 Healthy when DB is unreachable")]
    public async Task Alive_Returns_200_Even_When_DB_Is_Unreachable()
    {
        // Confirms liveness is independent of readiness: a DB outage must not mark the container
        // unhealthy or trigger restart loops for DB-independent pages (e.g. login page).
        await using var factory = new DeadDbFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/alive");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

/// <summary>
/// WebApplicationFactory that overrides all DbContext connection strings with a guaranteed-
/// unreachable host, exercising the real <c>AddDbContextCheck</c> failure path end-to-end.
/// No service mocking required — the production health check code runs as-is.
/// </summary>
file sealed class DeadDbFactory : WebApplicationFactory<Program>
{
    // Port 9 is the "discard" port: TCP connections are immediately dropped.
    // Using a valid connection string format that Npgsql accepts but that cannot succeed.
    private const string DeadConnStr =
        "Host=127.0.0.1;Port=9;Database=plantrydb;Username=app_user;Password=x;Timeout=1;CommandTimeout=1";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Override EVERY connection string to point at the dead host so no context can connect.
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Primary connection string used by Program.cs to derive appUserConnStr.
                ["ConnectionStrings:plantrydb"] = DeadConnStr,
                // Suppress DataProtection cert check in non-Production (Testing env skips it).
                ["DataProtection:KeyPath"] = Path.GetTempPath(),
            });
        });
    }
}
