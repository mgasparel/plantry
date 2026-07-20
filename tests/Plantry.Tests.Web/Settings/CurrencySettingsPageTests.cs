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
/// L4 fragment tests for the /Settings/Currency household display-currency picker (plantry-2x6e.1).
///
/// Verifies:
///   1. GET renders the picker with the household's persisted currency pre-selected.
///   2. POST a curated code persists it, returns the Saved badge, and a fresh GET reflects it.
///   3. POST a code outside the curated list is rejected and does not persist.
///   4. Unauthenticated GET returns 401.
///
/// The Household repository is stubbed in-memory so the page needs no Postgres; ITenantContext is
/// armed from the test auth header exactly as in production.
/// </summary>
[Trait("Category", "Web")]
public sealed class CurrencySettingsPageTests
{
    private static readonly Guid HouseholdId = Guid.Parse("cccccccc-0002-0000-0000-000000000009");

    // ── 1. GET reflects persisted currency ────────────────────────────────────

    [Fact(DisplayName = "L4: GET /Settings/Currency pre-selects the household's persisted currency")]
    public async Task Get_RendersPersistedCurrencySelected()
    {
        await using var factory = new CurrencyFactory("EUR");
        var client = MakeClient(factory);

        var response = await client.GetAsync("/Settings/Currency");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Display currency", html);
        Assert.Contains("value=\"EUR\" selected", html);
    }

    // ── 2. POST persists and confirms ─────────────────────────────────────────

    [Fact(DisplayName = "L4: POST /Settings/Currency persists the chosen currency and returns the Saved badge")]
    public async Task Post_PersistsCurrencyAndReturnsSavedBadge()
    {
        await using var factory = new CurrencyFactory("USD");
        var client = MakeClient(factory);

        var getResp = await client.GetAsync("/Settings/Currency");
        getResp.EnsureSuccessStatusCode();
        var token = ExtractAntiforgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent([
            new("__RequestVerificationToken", token),
            new("Currency", "GBP"),
        ]);

        var response = await client.PostAsync("/Settings/Currency", form);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Setting saved", html);
        Assert.Contains("value=\"GBP\" selected", html);

        // A fresh GET (same in-memory repo) confirms it persisted.
        var refetch = await client.GetAsync("/Settings/Currency");
        refetch.EnsureSuccessStatusCode();
        Assert.Contains("value=\"GBP\" selected", await refetch.Content.ReadAsStringAsync());
    }

    // ── 3. Non-curated code is rejected ───────────────────────────────────────

    [Fact(DisplayName = "L4: POST /Settings/Currency with a non-curated code does not persist")]
    public async Task Post_NonCuratedCode_Rejected()
    {
        await using var factory = new CurrencyFactory("USD");
        var client = MakeClient(factory);

        var getResp = await client.GetAsync("/Settings/Currency");
        getResp.EnsureSuccessStatusCode();
        var token = ExtractAntiforgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent([
            new("__RequestVerificationToken", token),
            new("Currency", "JPY"),
        ]);

        var response = await client.PostAsync("/Settings/Currency", form);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("Setting saved", html);

        // Unchanged: a fresh GET still shows USD selected.
        var refetch = await client.GetAsync("/Settings/Currency");
        refetch.EnsureSuccessStatusCode();
        Assert.Contains("value=\"USD\" selected", await refetch.Content.ReadAsStringAsync());
    }

    // ── 4. Unauthenticated returns 401 ────────────────────────────────────────

    [Fact(DisplayName = "L4: Unauthenticated GET /Settings/Currency returns 401")]
    public async Task Unauthenticated_Returns_401()
    {
        await using var factory = new CurrencyFactory("USD");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Settings/Currency");

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
        Assert.True(match.Success, "No antiforgery token found on the Settings/Currency page.");
        return match.Groups[1].Value;
    }

    // ── factory ──────────────────────────────────────────────────────────────

    private sealed class CurrencyFactory(string currency) : WebApplicationFactory<Program>
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
                household.SetDisplayCurrency(currency);

                services.RemoveAll<IHouseholdRepository>();
                services.AddSingleton<IHouseholdRepository>(new SingleHouseholdRepo(household));
            });
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    /// <summary>In-memory repo holding one mutable household, returned for any id lookup (single-tenant test).</summary>
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
