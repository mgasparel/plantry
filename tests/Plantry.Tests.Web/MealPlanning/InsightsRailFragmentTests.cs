using System.Text;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Identity.Infrastructure;
using Plantry.MealPlanning.Application;
using Plantry.Tests.Web.Infrastructure;
using Plantry.Tests.Web.Preferences;
using Xunit;

namespace Plantry.Tests.Web.MealPlanning;

/// <summary>
/// L4 fragment tests for the insights rail (<c>_PlanRail</c>) — P3-5.
///
/// Covers the two findings that parked plantry-6si:
///   FIX 2 — the rail fragment renders tones + action links + collapse controls + count badge.
///   FIX 1 — a cell-targeted mutation (assign) re-emits the rail out-of-band, so insights
///           recompute on EVERY change rather than only on a full page reload.
///
/// Uses the WAF harness with fake services (no Postgres). A single expiring product with an
/// empty plan guarantees the UnusedExpiring ("Use soon") warn callout fires.
/// </summary>
public sealed class InsightsRailFragmentTests : IClassFixture<InsightsRailFragmentFactory>
{
    private readonly InsightsRailFragmentFactory _factory;

    public InsightsRailFragmentTests(InsightsRailFragmentFactory factory) => _factory = factory;

    private HttpClient CreateClient()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, WeekGridFixture.HouseholdId.ToString());
        return client;
    }

    [Fact(DisplayName = "L4: rail renders a warn callout with a use-soon link, collapse controls, and a count badge")]
    public async Task Rail_Renders_Tones_Links_And_Collapse()
    {
        var client = CreateClient();

        var html = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var doc = new HtmlParser().ParseDocument(html);

        // (a) a warn-tone callout carrying the "Use soon" action link to the Recipes filter
        var warn = doc.QuerySelector("#plan-rail .callout[data-tone='warn']");
        Assert.NotNull(warn);
        var link = warn!.QuerySelector("a.co-link");
        Assert.NotNull(link);
        Assert.Equal("/Recipes?filter=use-soon", link!.GetAttribute("href"));

        // (b) collapse + reopen controls are present
        Assert.NotNull(doc.QuerySelector("#plan-rail .rail-collapse"));
        Assert.NotNull(doc.QuerySelector("#plan-rail-reopen"));

        // (c) the count badge equals the number of rendered callouts
        var calloutCount = doc.QuerySelectorAll("#plan-rail .callout").Length;
        var badge = doc.QuerySelector("#plan-rail .ri-count");
        Assert.NotNull(badge);
        Assert.Equal(calloutCount.ToString(), badge!.TextContent.Trim());
    }

    [Fact(DisplayName = "FIX 1: POST AssignJson recomputes the insights rail on every change (railHtml key)")]
    public async Task Assign_Reemits_Rail_OutOfBand()
    {
        var client = CreateClient();

        // GET first for the antiforgery token + paired cookie.
        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var slot = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();
        // Use a date in the current week so Plan Insights are active (plantry-lb9t: insights are
        // suppressed for past weeks). Monday of the current week is a safe choice — the server
        // also treats the current week as non-historical regardless of the exact weekday.
        var today = DateOnly.FromDateTime(DateTime.Today);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var payload = new { date = monday.ToString("yyyy-MM-dd"), slotId = slot.Id.Value, mode = "note", note = "Takeout" };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        content.Headers.Add("RequestVerificationToken", token);
        content.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var response = await client.PostAsync("/MealPlan?handler=AssignJson", content);
        response.EnsureSuccessStatusCode();
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var cellHtml = root.GetProperty("cellHtml").GetString() ?? "";
        var railHtml = root.GetProperty("railHtml").GetString() ?? "";

        // The mutated cell is the main swap target …
        Assert.Contains("mcell", cellHtml);

        // … and the rail rides along in its own JSON key — the ADR-013 contract, asserted through
        // the SAME shared primitive Intake's ReviewBoundaryTests uses, so "a mutation carries its
        // derived-view projection" is one enforced rule across features, not a per-feature reinvention.
        // (The island swaps railHtml into #plan-rail on every mutation; the inline/OOB grid path is
        // covered separately by FullGridSwap_Carries_Rail_Projection.)
        OobContract.AssertCarriesProjections(railHtml, "plan-rail");

        // The recomputed rail carries fresh insight content (active for the current week).
        var doc = new HtmlParser().ParseDocument(railHtml);
        Assert.NotNull(doc.QuerySelector("#plan-rail .callout[data-tone='warn']"));
    }

    [Fact(DisplayName = "plantry-lb9t: past-week rail shows inactive state instead of insight callouts")]
    public async Task HistoricalWeek_Rail_Shows_Inactive_State()
    {
        var client = CreateClient();

        // Navigate to a week that is clearly in the past (well before today).
        var html = await (await client.GetAsync("/MealPlan?week=2020-01-06")).Content.ReadAsStringAsync();
        var doc = new HtmlParser().ParseDocument(html);

        // The rail renders with the inactive modifier.
        var rail = doc.QuerySelector("#plan-rail");
        Assert.NotNull(rail);
        Assert.Contains("plan-rail--inactive", rail!.ClassName ?? "");

        // No callouts are rendered for a past week — insights are meaningless there.
        Assert.Empty(doc.QuerySelectorAll("#plan-rail .callout"));

        // The count badge is suppressed.
        Assert.Null(doc.QuerySelector("#plan-rail .ri-count"));

        // The reopen tab has no badge.
        Assert.Null(doc.QuerySelector("#plan-rail-reopen .rr-badge"));
    }

    [Fact(DisplayName = "Contract: the full-grid-swap path (Grid handler) also carries the rail projection")]
    public async Task FullGridSwap_Carries_Rail_Projection()
    {
        // Move / Generate / Accept / Discard / Accept-cell / Reject-cell all return the whole
        // _WeekGrid, which renders the rail inline (no hx-swap-oob). The contract is mechanism-
        // agnostic: whichever swap a plan mutation uses, the response must carry #plan-rail. This
        // pins the inline path so a future refactor can't quietly drop the rail from the grid.
        var client = CreateClient();

        var fragment = await (await client.GetAsync("/MealPlan?handler=Grid")).Content.ReadAsStringAsync();

        OobContract.AssertCarriesProjections(fragment, "plan-rail");
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the page.");
        return match.Groups[1].Value;
    }
}

/// <summary>
/// WAF variant: one expiring product (so the UnusedExpiring callout fires) on top of the
/// shared week-grid fakes, plus a stubbed UserManager for the POST Assign path.
/// </summary>
public sealed class InsightsRailFragmentFactory : WeekGridFragmentFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMealPlanExpiringStockReader>();
            services.AddSingleton<IMealPlanExpiringStockReader>(new OneExpiringProductReader());

            // POST Assign resolves the current user — stub UserManager off the Identity DB.
            services.RemoveAll<UserManager<AppUser>>();
            services.AddSingleton<UserManager<AppUser>>(
                new FakeUserManager(new AppUser { Id = "00000000-0000-0000-0000-0000000000aa" }));
        });
    }
}

/// <summary>Reports a single product as expiring soon, unconditionally.</summary>
internal sealed class OneExpiringProductReader : IMealPlanExpiringStockReader
{
    public Task<IReadOnlyList<Guid>> GetExpiringProductIdsAsync(DateOnly today, int withinDays, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Guid>>([Guid.Parse("dddddddd-0000-0000-0000-000000000001")]);
}
