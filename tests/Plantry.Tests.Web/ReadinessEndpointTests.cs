using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Plantry.Identity.Infrastructure;
using Plantry.SharedKernel.Tenancy;

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
/// WebApplicationFactory that points the identity DbContext at a guaranteed-unreachable host,
/// exercising the real <c>AddDbContextCheck&lt;PlantryIdentityDbContext&gt;</c> failure path
/// end-to-end. No service mocking required — the production health check code runs as-is.
///
/// The connection is redirected via a <see cref="ConfigureServices"/> re-registration of the
/// DbContext options, NOT a config override: <c>Program.cs</c> derives <c>appUserConnStr</c>
/// from <c>GetConnectionString("plantrydb")</c> in top-level code during host construction,
/// which runs <em>before</em> any <c>WebApplicationFactory</c> config merge — so neither
/// <c>ConfigureAppConfiguration</c> nor <c>UseSetting</c> can reach it. Swapping the resolved
/// <c>DbContextOptions&lt;PlantryIdentityDbContext&gt;</c> to the dead host is the only override
/// that takes effect, and it keeps the real check intact.
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

        // Suppress DataProtection cert check in non-Production (Testing env skips it).
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataProtection:KeyPath"] = Path.GetTempPath(),
            });
        });

        // Redirect the identity context to the dead host (see class doc for why config override
        // is insufficient). Mirrors Program.cs's registration — same migration assembly and RLS
        // interceptor — so the health check exercises the real options pipeline, just against
        // an unreachable endpoint.
        builder.ConfigureServices(services =>
        {
            var options = services.Single(d =>
                d.ServiceType == typeof(DbContextOptions<PlantryIdentityDbContext>));
            services.Remove(options);
            services.AddDbContext<PlantryIdentityDbContext>((sp, opts) =>
                opts.UseNpgsql(DeadConnStr,
                        npgsql => npgsql.MigrationsAssembly("Plantry.Identity.Infrastructure"))
                    .AddInterceptors(sp.GetRequiredService<HouseholdRlsConnectionInterceptor>()));
        });
    }
}
