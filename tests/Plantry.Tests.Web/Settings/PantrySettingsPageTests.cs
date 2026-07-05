using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Settings;

/// <summary>
/// L4 WebApplicationFactory tests for the /Settings/Pantry expiring-soon horizon page (plantry-1q7d,
/// closing a coverage gap on plantry-5yhd). The sibling /Settings/MealPlanning already has this L4
/// coverage; this proves the horizon is genuinely "settable via the UI" end-to-end.
///
/// Test seam: the page injects the CONCRETE sealed <see cref="Plantry.Inventory.Application.ExpiringSoonSettingsService"/>,
/// so faking <c>IExpiringSoonHorizon</c> would not intercept it. Instead we replace the single port
/// the real service reaches for the database through — <see cref="IHouseholdInventorySettingsRepository"/>
/// — with an in-memory fake, and let the REAL service run. This exercises page binding, [Range]
/// ModelState validation, the service's load-or-create logic, and lazy row seeding together, with no
/// Postgres required.
///
/// Verifies:
///   1. GET with no settings row → 200, form renders, input shows the Inventory default.
///   2. GET with a seeded non-default row → input round-trips the persisted value (reads state, not default).
///   3. Valid POST → 200, fake repo holds the posted value, response shows the Saved badge + value.
///   4. Out-of-range POST (Max+1) → 200 re-render with the field error, NO write, no Saved badge.
///   5. Unauthenticated GET (no household header) → 401.
/// </summary>
[Trait("Category", "Web")]
public sealed class PantrySettingsPageTests
{
    // ── 1. GET, no row → default value rendered ──────────────────────────────

    [Fact(DisplayName = "L4: GET /Settings/Pantry with no settings row renders the default horizon")]
    public async Task Get_NoRow_ShowsDefault()
    {
        await using var factory = new PantrySettingsFactory();
        var client = MakeClient(factory);

        var response = await client.GetAsync("/Settings/Pantry");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Pantry settings", html);
        // Input is populated from the service, which falls back to the Inventory default when no row exists.
        Assert.Contains($"value=\"{HouseholdInventorySettings.DefaultExpiringSoonDays}\"", html);
    }

    // ── 2. GET, seeded non-default row → round-trips persisted value ──────────

    [Fact(DisplayName = "L4: GET /Settings/Pantry with a seeded non-default row round-trips the persisted value")]
    public async Task Get_SeededRow_RoundTripsValue()
    {
        const int seeded = 10; // distinct from the default (7) so the assertion proves a real read
        await using var factory = new PantrySettingsFactory();
        factory.Repo.Seed(PantrySettingsFixture.HouseholdId, seeded);
        var client = MakeClient(factory);

        var response = await client.GetAsync("/Settings/Pantry");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains($"value=\"{seeded}\"", html);
        Assert.DoesNotContain($"value=\"{HouseholdInventorySettings.DefaultExpiringSoonDays}\"", html);
    }

    // ── 3. Valid POST → persists, shows Saved badge + value ──────────────────

    [Fact(DisplayName = "L4: valid POST /Settings/Pantry persists the horizon and shows the Saved badge")]
    public async Task Post_ValidValue_PersistsAndConfirms()
    {
        const int posted = 3; // in range, distinct from the default
        await using var factory = new PantrySettingsFactory();
        var client = MakeClient(factory);

        // GET first for a valid antiforgery token.
        var getResp = await client.GetAsync("/Settings/Pantry");
        getResp.EnsureSuccessStatusCode();
        var token = ExtractAntiforgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent([
            new("__RequestVerificationToken", token),
            new("Input.ExpiringSoonDays", posted.ToString()),
        ]);

        var response = await client.PostAsync("/Settings/Pantry", form);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // The real service seeded a row in the fake repo holding the posted value.
        Assert.Equal(posted, Assert.Single(factory.Repo.Items).ExpiringSoonDays);
        // The page reflects the saved state.
        Assert.Contains("Setting saved", html);
        Assert.Contains($"value=\"{posted}\"", html);
    }

    // ── 4. Out-of-range POST → re-render with error, no write ─────────────────

    [Fact(DisplayName = "L4: out-of-range POST /Settings/Pantry re-renders the field error and writes nothing")]
    public async Task Post_OutOfRange_ShowsErrorAndDoesNotWrite()
    {
        const int outOfRange = HouseholdInventorySettings.MaxExpiringSoonDays + 1;
        await using var factory = new PantrySettingsFactory();
        var client = MakeClient(factory);

        var getResp = await client.GetAsync("/Settings/Pantry");
        getResp.EnsureSuccessStatusCode();
        var token = ExtractAntiforgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent([
            new("__RequestVerificationToken", token),
            new("Input.ExpiringSoonDays", outOfRange.ToString()),
        ]);

        var response = await client.PostAsync("/Settings/Pantry", form);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // [Range] failed → ModelState invalid → OnPostAsync returned Page() before touching the service.
        var expectedError =
            $"Choose between {HouseholdInventorySettings.MinExpiringSoonDays} and {HouseholdInventorySettings.MaxExpiringSoonDays} days.";
        Assert.Contains(expectedError, html);
        Assert.DoesNotContain("Setting saved", html);
        Assert.Empty(factory.Repo.Items);
    }

    // ── 5. Unauthenticated GET → 401 ─────────────────────────────────────────

    [Fact(DisplayName = "L4: unauthenticated GET /Settings/Pantry returns 401")]
    public async Task Unauthenticated_Returns_401()
    {
        await using var factory = new PantrySettingsFactory();
        // No household header → TestAuthHandler returns no result → [Authorize] rejects.
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Settings/Pantry");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static HttpClient MakeClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, PantrySettingsFixture.HouseholdId.ToString());
        return client;
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the /Settings/Pantry page.");
        return match.Groups[1].Value;
    }
}

// ── Fixture ──────────────────────────────────────────────────────────────────

public static class PantrySettingsFixture
{
    public static readonly Guid HouseholdId = Guid.Parse("dddddddd-0002-0000-0000-000000000001");
}

// ── Factory ──────────────────────────────────────────────────────────────────

/// <summary>
/// Boots the real Plantry.Web pipeline in the "Testing" environment, swaps in the header-driven
/// <see cref="TestAuthHandler"/> auth scheme, and replaces the Inventory settings repository with an
/// in-memory fake while leaving the real <c>ExpiringSoonSettingsService</c> in place. The fake is a
/// singleton exposed via <see cref="Repo"/> so a test can seed rows and inspect writes.
/// </summary>
public sealed class PantrySettingsFactory : WebApplicationFactory<Program>
{
    public FakeInventorySettingsRepository Repo { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // Auth: header-driven test scheme (arms the real RlsMiddleware / TenantContext).
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Replace ONLY the database port; the real ExpiringSoonSettingsService still runs.
            services.RemoveAll<IHouseholdInventorySettingsRepository>();
            services.AddSingleton<IHouseholdInventorySettingsRepository>(Repo);
        });
    }
}

// ── In-memory settings repository ──────────────────────────────────────────────

/// <summary>
/// Local Tests.Web equivalent of the Tests.Unit FakeHouseholdInventorySettingsRepository (which is
/// internal to that assembly). In-memory, keyed by household; lets the real service perform its
/// load-or-create upsert so tests can seed state and inspect what the service wrote.
/// </summary>
public sealed class FakeInventorySettingsRepository : IHouseholdInventorySettingsRepository
{
    public List<HouseholdInventorySettings> Items { get; } = [];
    public int SaveChangesCalls { get; private set; }

    /// <summary>Seeds a persisted horizon for a household ahead of a request.</summary>
    public void Seed(Guid householdId, int days)
    {
        var settings = HouseholdInventorySettings.Create(HouseholdId.From(householdId));
        settings.SetExpiringSoonDays(days);
        Items.Add(settings);
    }

    public Task<HouseholdInventorySettings?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(s => s.HouseholdId == householdId));

    public Task AddAsync(HouseholdInventorySettings settings, CancellationToken ct = default)
    {
        Items.Add(settings);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCalls++;
        return Task.CompletedTask;
    }
}
