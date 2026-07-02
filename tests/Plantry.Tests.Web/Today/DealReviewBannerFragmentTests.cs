using System.Net;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Today;

/// <summary>
/// L4 fragment tests for the Phase-5 <b>deal-review</b> banner on the Today page (plantry-bpw / DJ4 /
/// SPEC §0b). The banner is an <b>additive</b> kind in the existing kind-keyed stack (plantry-yb6):
/// its pending count is recomputed live via <c>BrowseDeals</c> (Pending ∧ in-window, DD14), and it
/// deep-links into the P5-8 review queue at <c>/Deals/Review</c>.
///
/// In-window variant: one Pending in-window deal + one Ready intake session →
/// both the deal banner and the (untouched) intake banner render.
/// </summary>
public sealed class DealReviewBannerInWindowTests(TodayDealBannerFactory factory)
    : IClassFixture<TodayDealBannerFactory>
{
    private static readonly HtmlParser Parser = new();

    private async Task<string> GetTodayPageAsync()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            TodayReviewBannerFixture.HouseholdAId.ToString());
        var response = await client.GetAsync("/Today");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact(DisplayName = "DealBanner — a pending in-window deal renders a 'deal' kind banner")]
    public async Task DealBanner_PendingInWindow_RendersDealKindBanner()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);

        var dealBanner = doc.QuerySelector(".today-banner--deal");
        Assert.NotNull(dealBanner);
    }

    [Fact(DisplayName = "DealBanner — title mentions the pending count and 'review'")]
    public async Task DealBanner_Title_MentionsCountAndReview()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);

        var title = doc.QuerySelector(".today-banner--deal .today-banner__title");
        Assert.NotNull(title);
        var text = title.TextContent;
        Assert.Contains("1", text, StringComparison.OrdinalIgnoreCase);       // one pending in-window deal
        Assert.Contains("review", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "DealBanner — Review action deep-links into the P5-8 queue (/Deals/Review)")]
    public async Task DealBanner_ReviewLink_DeepLinksToDealsReview()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);

        var reviewLink = doc.QuerySelector(".today-banner--deal .today-banner__review");
        Assert.NotNull(reviewLink);
        Assert.Equal(
            Plantry.Web.Pages.Deals.IndexModel.ReviewQueueUrl,
            reviewLink.GetAttribute("href"));
    }

    [Fact(DisplayName = "DealBanner — is additive: the intake banner still renders alongside it")]
    public async Task DealBanner_IsAdditive_IntakeBannerUntouched()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);

        // Both kinds present in the one kind-keyed stack.
        Assert.NotNull(doc.QuerySelector(".today-banner--intake"));
        Assert.NotNull(doc.QuerySelector(".today-banner--deal"));
        Assert.Equal(2, doc.QuerySelectorAll(".today-banner").Length);
    }
}

/// <summary>
/// L4 fragment tests — all-expired variant. The only Pending deal has a closed window, so
/// <c>BrowseDeals</c> recomputes zero pending-in-window and no banner renders. Proves the count is
/// clock-driven (DD14), never the stamped <c>FlyerImported.pendingCount</c>.
/// </summary>
public sealed class DealReviewBannerExpiredTests(TodayDealBannerExpiredFactory factory)
    : IClassFixture<TodayDealBannerExpiredFactory>
{
    private static readonly HtmlParser Parser = new();

    private async Task<string> GetTodayPageAsync()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            TodayReviewBannerFixture.HouseholdAId.ToString());
        var response = await client.GetAsync("/Today");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact(DisplayName = "DealBanner — an all-expired pending set renders no deal banner")]
    public async Task DealBanner_AllExpired_NoDealBanner()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);

        Assert.Null(doc.QuerySelector(".today-banner--deal"));
        // No intake session either, so no banner chrome at all.
        Assert.Null(doc.QuerySelector(".today-banner-stack"));
        Assert.Empty(doc.QuerySelectorAll(".today-banner"));
    }

    [Fact(DisplayName = "DealBanner — the Today page still renders normally with an expired deal")]
    public async Task DealBanner_AllExpired_PageRendersNormally()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);

        Assert.NotNull(doc.QuerySelector(".today-wrap"));
    }
}
