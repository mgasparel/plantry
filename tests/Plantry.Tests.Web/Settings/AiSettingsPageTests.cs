using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Identity.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Settings;

/// <summary>
/// L4 fragment tests for the /Settings/Ai household "AI assistance" switch (plantry-qll2.1).
///
/// Verifies:
///   1. GET renders the switch ON for a household with AI assistance enabled (the default).
///   2. GET renders the switch OFF for a household that has disabled it.
///   3. POST Enabled=false persists OFF, returns the Saved badge, and reflects the OFF state.
///   4. Unauthenticated GET returns 401.
///
/// The Household repository is stubbed in-memory so the page needs no Postgres; ITenantContext is
/// armed from the test auth header exactly as in production.
/// </summary>
[Trait("Category", "Web")]
public sealed class AiSettingsPageTests
{
    private static readonly Guid HouseholdId = Guid.Parse("cccccccc-0001-0000-0000-000000000009");

    // ── 1. Default ON ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "L4: GET /Settings/Ai renders the switch ON for an enabled household")]
    public async Task Get_EnabledHousehold_RendersOn()
    {
        await using var factory = new AiSettingsFactory(enabled: true);
        var client = MakeClient(factory);

        var response = await client.GetAsync("/Settings/Ai");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("AI assistance", html);
        Assert.Contains("seg-ctrl", html);
        Assert.Contains("value=\"true\" checked", html);
    }

    // ── 2. OFF household ──────────────────────────────────────────────────────

    [Fact(DisplayName = "L4: GET /Settings/Ai renders the switch OFF for a disabled household")]
    public async Task Get_DisabledHousehold_RendersOff()
    {
        await using var factory = new AiSettingsFactory(enabled: false);
        var client = MakeClient(factory);

        var response = await client.GetAsync("/Settings/Ai");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("value=\"false\" checked", html);
    }

    // ── 3. POST persists OFF and returns the Saved badge ──────────────────────

    [Fact(DisplayName = "L4: POST /Settings/Ai with Enabled=false persists OFF and returns the Saved badge")]
    public async Task Post_DisableAi_PersistsAndReturnsSavedBadge()
    {
        await using var factory = new AiSettingsFactory(enabled: true);
        var client = MakeClient(factory);

        var getResp = await client.GetAsync("/Settings/Ai");
        getResp.EnsureSuccessStatusCode();
        var token = ExtractAntiforgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent([
            new("__RequestVerificationToken", token),
            new("Enabled", "false"),
        ]);

        var response = await client.PostAsync("/Settings/Ai", form);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Setting saved", html);
        // Persisted state is reflected: the OFF option is now the checked one.
        Assert.Contains("value=\"false\" checked", html);

        // And a fresh GET (same in-memory repo) confirms the switch stayed OFF.
        var refetch = await client.GetAsync("/Settings/Ai");
        refetch.EnsureSuccessStatusCode();
        Assert.Contains("value=\"false\" checked", await refetch.Content.ReadAsStringAsync());
    }

    // ── 4. Unauthenticated returns 401 ────────────────────────────────────────

    [Fact(DisplayName = "L4: Unauthenticated GET /Settings/Ai returns 401")]
    public async Task Unauthenticated_Returns_401()
    {
        await using var factory = new AiSettingsFactory(enabled: true);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Settings/Ai");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static HttpClient MakeClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdId.ToString());
        return client;
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the Settings/Ai page.");
        return match.Groups[1].Value;
    }

    // ── factory ──────────────────────────────────────────────────────────────

    private sealed class AiSettingsFactory(bool enabled) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureTestServices(services =>
            {
                services.AddFakeExpiringSoonHorizon();

                services.AddAuthentication(opts =>
                    {
                        opts.DefaultScheme = TestAuthHandler.SchemeName;
                        opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                        opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

                var household = Household.Create("Test", new FixedClock(DateTimeOffset.UnixEpoch));
                if (!enabled) household.SetAiAssistanceEnabled(false);

                services.RemoveAll<IHouseholdRepository>();
                services.AddSingleton<IHouseholdRepository>(new SingleHouseholdRepo(household));
            });
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    /// <summary>In-memory repo holding one household, returned for any id lookup (single-tenant test).</summary>
    private sealed class SingleHouseholdRepo(Household household) : IHouseholdRepository
    {
        public Task<Household?> FindAsync(HouseholdId id, CancellationToken ct = default) =>
            Task.FromResult<Household?>(household);

        public Task<IReadOnlyList<HouseholdId>> ListAllIdsAsync(CancellationToken ct = default) =>
            Task.FromResult((IReadOnlyList<HouseholdId>)new[] { household.Id });

        public Task AddAsync(Household h, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
