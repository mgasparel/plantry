using System.Net;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Today;

/// <summary>
/// L4 fragment tests for the Today expiring-soon widget's "Use these up" foot action (plantry-w1e).
///
/// The action is a deep link into the Recipes browse pre-filtered to "use soon", and it is rendered
/// <b>only</b> in the widget's populated state. These tests assert the negative half of the acceptance
/// criterion — the action is <b>absent</b> in the cold-start and all-clear states. Its presence in the
/// populated state, and that the deep link lands on the correctly-filtered browse, are proven end-to-end
/// by <c>TodayExpiringSoonSmokeTests</c> against real data.
/// </summary>
public sealed class ExpiringWidgetActionTests
{
    private static readonly HtmlParser Parser = new();

    /// <summary>The deep-link href the "Use these up" CTA carries in the populated state.</summary>
    private const string UseUpHref = "/Recipes?soon=true";

    private static async Task<string> GetTodayPageAsync(TodayExpiringWidgetFactoryBase factory)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, Guid.NewGuid().ToString());
        var response = await client.GetAsync("/Today");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    // ── All-clear state: stock exists, nothing expiring ───────────────────────

    public sealed class AllClear(TodayExpiringWidgetAllClearFactory factory)
        : IClassFixture<TodayExpiringWidgetAllClearFactory>
    {
        [Fact(DisplayName = "All-clear widget — expiring widget renders but has no 'Use these up' action")]
        public async Task AllClear_NoUseUpAction()
        {
            var html = await GetTodayPageAsync(factory);
            var doc = Parser.ParseDocument(html);

            // The widget is present (household has stock, so not cold-start)…
            Assert.NotNull(doc.QuerySelector(".today-exp-widget"));
            // …but in the all-clear state, so no deep link to the filtered browse.
            Assert.Null(doc.QuerySelector(".today-widget__foot-cta"));
            Assert.DoesNotContain(UseUpHref, html, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── Cold-start state: no stock, recipes, or pending intake ────────────────

    public sealed class ColdStart(TodayExpiringWidgetColdStartFactory factory)
        : IClassFixture<TodayExpiringWidgetColdStartFactory>
    {
        [Fact(DisplayName = "Cold-start widget — no 'Use these up' action rendered")]
        public async Task ColdStart_NoUseUpAction()
        {
            var html = await GetTodayPageAsync(factory);
            var doc = Parser.ParseDocument(html);

            Assert.Null(doc.QuerySelector(".today-widget__foot-cta"));
            Assert.DoesNotContain(UseUpHref, html, StringComparison.OrdinalIgnoreCase);
        }
    }
}
