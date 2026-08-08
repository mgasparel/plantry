using System.Net;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Today;

/// <summary>
/// L4 fragment tests for the Today stats widget (plantry-h9z9, stats-page-prototype.html appendix
/// "Today" injection point). Fetches the real Today page through the WAF (full pipeline, in-memory
/// fakes) and asserts on the rendered <c>#today-stats-widget</c> fragment — mirroring
/// <c>ExpiringWidgetActionTests</c>'s shape (a shared helper typed on the common
/// <see cref="TodayStatsWidgetFactoryBase"/>) for the sibling widget.
/// </summary>
public sealed class TodayStatsWidgetFragmentTests
{
    private static readonly HtmlParser Parser = new();

    private static async Task<string> GetTodayPageAsync(TodayStatsWidgetFactoryBase factory)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, Guid.NewGuid().ToString());
        var response = await client.GetAsync("/Today");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    // ── Cold-start state: whole board (widget included) is absent ─────────────

    public sealed class ColdStart(TodayStatsWidgetColdStartFactory factory)
        : IClassFixture<TodayStatsWidgetColdStartFactory>
    {
        [Fact(DisplayName = "Cold start — stats widget is absent")]
        public async Task ColdStart_WidgetAbsent()
        {
            var html = await GetTodayPageAsync(factory);
            var doc = Parser.ParseDocument(html);

            Assert.Null(doc.QuerySelector("#today-stats-widget"));
        }
    }

    // ── Non-cold-start, no waste/discard history ──────────────────────────────

    public sealed class NoChips(TodayStatsWidgetNoChipsFactory factory)
        : IClassFixture<TodayStatsWidgetNoChipsFactory>
    {
        [Fact(DisplayName = "Non-cold-start, no discard history — widget renders a rotating fact, no chips")]
        public async Task NoDiscardHistory_RendersFactNoChips()
        {
            var html = await GetTodayPageAsync(factory);
            var doc = Parser.ParseDocument(html);

            var widget = doc.QuerySelector("#today-stats-widget");
            Assert.NotNull(widget);

            var fact = widget!.QuerySelector(".today-cta__sub");
            Assert.NotNull(fact);
            Assert.False(string.IsNullOrWhiteSpace(fact!.TextContent));

            Assert.Null(widget.QuerySelector(".chip-stat"));
        }
    }

    // ── Non-cold-start, a recent discard on record ────────────────────────────

    public sealed class WithChips(TodayStatsWidgetWithChipsFactory factory)
        : IClassFixture<TodayStatsWidgetWithChipsFactory>
    {
        [Fact(DisplayName = "A recent discard renders the 'days since anything expired' streak chip")]
        public async Task RecentDiscard_RendersStreakChip()
        {
            var html = await GetTodayPageAsync(factory);
            var doc = Parser.ParseDocument(html);

            var widget = doc.QuerySelector("#today-stats-widget");
            Assert.NotNull(widget);

            var chip = widget!.QuerySelector(".chip-stat");
            Assert.NotNull(chip);
            Assert.Contains("since anything expired", chip!.TextContent, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("4", chip.TextContent, StringComparison.Ordinal);
        }
    }
}
