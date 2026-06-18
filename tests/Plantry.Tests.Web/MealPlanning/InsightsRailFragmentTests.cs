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

    [Fact(DisplayName = "FIX 1: POST Assign re-emits the insights rail out-of-band (recompute on every change)")]
    public async Task Assign_Reemits_Rail_OutOfBand()
    {
        var client = CreateClient();

        // GET first for the antiforgery token + paired cookie.
        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var slot = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            new KeyValuePair<string, string>("mode", "note"),
            new KeyValuePair<string, string>("note", "Takeout"),
        });

        var response = await client.PostAsync($"/MealPlan?handler=Assign&date=2026-06-01&slotId={slot.Id.Value:D}", form);
        response.EnsureSuccessStatusCode();
        var fragment = await response.Content.ReadAsStringAsync();

        var doc = new HtmlParser().ParseDocument(fragment);

        // The mutated cell is the main swap target …
        Assert.Contains("mcell", fragment);

        // … and the rail rides along as an out-of-band refresh so it can never go stale.
        var rail = doc.QuerySelector("#plan-rail");
        Assert.NotNull(rail);
        Assert.Equal("true", rail!.GetAttribute("hx-swap-oob"));
        Assert.NotNull(doc.QuerySelector("#plan-rail .callout[data-tone='warn']"));
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
