using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Identity.Application;
using Plantry.Identity.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Settings;

/// <summary>
/// L4 WebApplicationFactory tests for the /Settings/Expiry page (plantry-qckx): the household's
/// after-freezing/after-thawing due-days defaults (plantry-hh1f).
///
/// Test seam: the page injects the CONCRETE sealed <see cref="HouseholdExpiryDefaultsService"/>, so
/// faking <see cref="IHouseholdExpiryDefaults"/> would not intercept it. Instead we replace the single
/// port the real service reaches for the database through — <see cref="IHouseholdRepository"/> — with
/// an in-memory single-household fake, and let the REAL service run. Mirrors
/// <c>CurrencySettingsPageTests</c>'s and <c>PantrySettingsPageTests</c>'s shape.
///
/// Verifies:
///   1. GET with a freshly created household renders the baked-in defaults (90/3).
///   2. GET with a household that has edited values round-trips the persisted values.
///   3. Valid POST persists both fields, shows the Saved badge, and a fresh GET reflects them.
///   4. Out-of-range POST (Max+1) re-renders the field error, writes nothing.
///   5. Unauthenticated GET returns 401.
///   6. The page does NOT offer an expiry-warning input and links to /Settings/Pantry instead —
///      plantry-qckx retired the household's per-row "expiry warning days" column as dead
///      configuration duplicating the Inventory context's live "expiring soon" horizon.
/// </summary>
[Trait("Category", "Web")]
public sealed class ExpirySettingsPageTests
{
    private static readonly Guid HouseholdId = Guid.Parse("cccccccc-0004-0000-0000-000000000001");
    private static readonly HtmlParser Parser = new();

    // ── 1. GET, freshly created household → baked-in defaults ────────────────

    [Fact(DisplayName = "L4: GET /Settings/Expiry with a fresh household renders the 90/3 defaults")]
    public async Task Get_FreshHousehold_ShowsDefaults()
    {
        await using var factory = new ExpiryFactory();
        var client = MakeClient(factory);

        var response = await client.GetAsync("/Settings/Expiry");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Expiry defaults", html);
        var doc = Parser.ParseDocument(html);
        // Per-input assertions — proves each value lands in ITS OWN field, not merely somewhere
        // on the page.
        Assert.Equal("90", doc.QuerySelector("#Input_AfterFreezingDays")!.GetAttribute("value"));
        Assert.Equal("3", doc.QuerySelector("#Input_AfterThawingDays")!.GetAttribute("value"));
    }

    // ── 2. GET, edited household → round-trips persisted values ──────────────

    [Fact(DisplayName = "L4: GET /Settings/Expiry with an edited household round-trips the persisted values")]
    public async Task Get_EditedHousehold_RoundTripsValues()
    {
        await using var factory = new ExpiryFactory(afterFreezing: 45, afterThawing: 6);
        var client = MakeClient(factory);

        var response = await client.GetAsync("/Settings/Expiry");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        var doc = Parser.ParseDocument(html);
        Assert.Equal("45", doc.QuerySelector("#Input_AfterFreezingDays")!.GetAttribute("value"));
        Assert.Equal("6", doc.QuerySelector("#Input_AfterThawingDays")!.GetAttribute("value"));
        Assert.DoesNotContain("value=\"90\"", html);
    }

    // ── 3. Valid POST → persists both fields, shows Saved badge ──────────────

    [Fact(DisplayName = "L4: valid POST /Settings/Expiry persists both fields and shows the Saved badge")]
    public async Task Post_ValidValues_PersistsAndConfirms()
    {
        await using var factory = new ExpiryFactory();
        var client = MakeClient(factory);

        var getResp = await client.GetAsync("/Settings/Expiry");
        getResp.EnsureSuccessStatusCode();
        var token = ExtractAntiforgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent([
            new("__RequestVerificationToken", token),
            new("Input.AfterFreezingDays", "120"),
            new("Input.AfterThawingDays", "7"),
        ]);

        var response = await client.PostAsync("/Settings/Expiry", form);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Setting saved", html);
        var doc = Parser.ParseDocument(html);
        Assert.Equal("120", doc.QuerySelector("#Input_AfterFreezingDays")!.GetAttribute("value"));
        Assert.Equal("7", doc.QuerySelector("#Input_AfterThawingDays")!.GetAttribute("value"));
        Assert.Equal(120, factory.Household.DefaultDueDaysAfterFreezing);
        Assert.Equal(7, factory.Household.DefaultDueDaysAfterThawing);

        // A fresh GET (same in-memory repo) confirms it persisted.
        var refetch = await client.GetAsync("/Settings/Expiry");
        refetch.EnsureSuccessStatusCode();
        var refetchDoc = Parser.ParseDocument(await refetch.Content.ReadAsStringAsync());
        Assert.Equal("120", refetchDoc.QuerySelector("#Input_AfterFreezingDays")!.GetAttribute("value"));
    }

    // ── 4. Out-of-range POST → re-render with error, no write ─────────────────

    [Fact(DisplayName = "L4: out-of-range POST /Settings/Expiry re-renders the field error and writes nothing")]
    public async Task Post_OutOfRange_ShowsErrorAndDoesNotWrite()
    {
        var outOfRange = HouseholdExpiryDefaultsService.MaxDays + 1;
        await using var factory = new ExpiryFactory();
        var client = MakeClient(factory);

        var getResp = await client.GetAsync("/Settings/Expiry");
        getResp.EnsureSuccessStatusCode();
        var token = ExtractAntiforgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent([
            new("__RequestVerificationToken", token),
            new("Input.AfterFreezingDays", outOfRange.ToString()),
            new("Input.AfterThawingDays", "3"),
        ]);

        var response = await client.PostAsync("/Settings/Expiry", form);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        var expectedError = $"Choose between {HouseholdExpiryDefaultsService.MinDays} and {HouseholdExpiryDefaultsService.MaxDays} days.";
        // Id-keyed, not a page-wide Contains: the same string is also emitted as the
        // data-val-range client-side validation attribute on every render, so a bare
        // Assert.Contains would stay green even with the server-rendered error span deleted.
        var errDoc = Parser.ParseDocument(html);
        var errSpan = errDoc.QuerySelector("span[data-valmsg-for='Input.AfterFreezingDays']");
        Assert.NotNull(errSpan);
        Assert.Equal(expectedError, errSpan!.TextContent.Trim());
        Assert.DoesNotContain("Setting saved", html);
        // [Range] failed → ModelState invalid → OnPostAsync returned Page() before touching the service.
        Assert.Equal(90, factory.Household.DefaultDueDaysAfterFreezing);
    }

    // ── 5. Unauthenticated GET → 401 ─────────────────────────────────────────

    [Fact(DisplayName = "L4: unauthenticated GET /Settings/Expiry returns 401")]
    public async Task Unauthenticated_Returns_401()
    {
        await using var factory = new ExpiryFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Settings/Expiry");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── 6. No expiry-warning input; links to /Settings/Pantry instead ────────

    [Fact(DisplayName = "L4: GET /Settings/Expiry offers no expiry-warning input and links to /Settings/Pantry")]
    public async Task Get_HasNoWarningInput_LinksToPantry()
    {
        await using var factory = new ExpiryFactory();
        var client = MakeClient(factory);

        var response = await client.GetAsync("/Settings/Expiry");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        var doc = Parser.ParseDocument(html);
        Assert.Null(doc.QuerySelector("#Input_WarningDays"));
        var pantryLink = doc.QuerySelectorAll("a").FirstOrDefault(a => a.GetAttribute("href") == "/Settings/Pantry");
        Assert.NotNull(pantryLink);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

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
        Assert.True(match.Success, "No antiforgery token found on the /Settings/Expiry page.");
        return match.Groups[1].Value;
    }

    // ── factory ──────────────────────────────────────────────────────────────

    private sealed class ExpiryFactory(int? afterFreezing = null, int? afterThawing = null)
        : WebApplicationFactory<Program>
    {
        public Household Household { get; } = BuildHousehold(afterFreezing, afterThawing);

        private static Household BuildHousehold(int? afterFreezing, int? afterThawing)
        {
            var household = Household.Create("Test", new FixedClock(DateTimeOffset.UnixEpoch));
            if (afterFreezing is { } f) household.SetDefaultDueDaysAfterFreezing(f);
            if (afterThawing is { } t) household.SetDefaultDueDaysAfterThawing(t);
            return household;
        }

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

                services.RemoveAll<IHouseholdRepository>();
                services.AddSingleton<IHouseholdRepository>(new SingleHouseholdRepo(Household));
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
