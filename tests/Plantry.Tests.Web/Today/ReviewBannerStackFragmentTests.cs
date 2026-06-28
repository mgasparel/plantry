using System.Net;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Today;

/// <summary>
/// L4 fragment tests for the review-banner stack on the Today page (SPEC Page 0 §0b, plantry-yb6).
///
/// Three state variants tested:
/// <list type="bullet">
///   <item><b>One banner</b> — one Ready session renders one banner with correct title, sub-text,
///     action link to /Intake/Review/{id}, and dismiss button.</item>
///   <item><b>Many banners</b> — two Ready sessions render two banners in the stack.</item>
///   <item><b>None</b> — no Ready sessions renders no banner chrome at all.</item>
/// </list>
/// </summary>
public sealed class ReviewBannerStackOneSessionTests(TodayReviewBannerOneFactory factory)
    : IClassFixture<TodayReviewBannerOneFactory>
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

    [Fact(DisplayName = "BannerStack — one Ready session renders exactly one banner")]
    public async Task BannerStack_OneReadySession_RendersOneBanner()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);

        var banners = doc.QuerySelectorAll(".today-banner");
        Assert.Single(banners);
    }

    [Fact(DisplayName = "BannerStack — banner has the 'intake' kind modifier class")]
    public async Task BannerStack_Banner_HasIntakeKindClass()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);

        var banner = doc.QuerySelector(".today-banner--intake");
        Assert.NotNull(banner);
    }

    [Fact(DisplayName = "BannerStack — banner title mentions the item count and store name")]
    public async Task BannerStack_Banner_TitleMentionsItemCountAndStore()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);

        var title = doc.QuerySelector(".today-banner__title");
        Assert.NotNull(title);
        var text = title.TextContent;
        // Fixture: 3 lines, "Whole Foods"
        Assert.Contains("3", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Whole Foods", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "BannerStack — banner sub-text mentions 'Forwarded by email'")]
    public async Task BannerStack_Banner_SubTextMentionsForwardedByEmail()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);

        var sub = doc.QuerySelector(".today-banner__sub");
        Assert.NotNull(sub);
        Assert.Contains("Forwarded by email", sub.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "BannerStack — banner has a Review action link pointing to /Intake/Review/...")]
    public async Task BannerStack_Banner_HasReviewActionLink()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);

        var reviewLink = doc.QuerySelector(".today-banner__review");
        Assert.NotNull(reviewLink);
        var href = reviewLink.GetAttribute("href") ?? "";
        Assert.Matches(@"/Intake/Review/[0-9a-f\-]+", href);
    }

    [Fact(DisplayName = "BannerStack — banner has a dismiss button")]
    public async Task BannerStack_Banner_HasDismissButton()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);

        var dismissBtn = doc.QuerySelector(".today-banner__dismiss");
        Assert.NotNull(dismissBtn);
        Assert.Equal("button", dismissBtn.TagName.ToLowerInvariant());
    }

    [Fact(DisplayName = "BannerStack — banner stack wrapper has Alpine x-data attribute")]
    public async Task BannerStack_Wrapper_HasAlpineXData()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);

        var stack = doc.QuerySelector(".today-banner-stack");
        Assert.NotNull(stack);
        Assert.NotNull(stack.GetAttribute("x-data"));
    }

    [Fact(DisplayName = "BannerStack — banner has Alpine x-show attribute for dismissal")]
    public async Task BannerStack_Banner_HasAlpineXShow()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);

        var banner = doc.QuerySelector(".today-banner");
        Assert.NotNull(banner);
        Assert.NotNull(banner.GetAttribute("x-show"));
    }
}

/// <summary>
/// L4 fragment tests for the review-banner stack — many-sessions variant.
/// </summary>
public sealed class ReviewBannerStackManySessionsTests(TodayReviewBannerManyFactory factory)
    : IClassFixture<TodayReviewBannerManyFactory>
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

    [Fact(DisplayName = "BannerStack — two Ready sessions render two banners")]
    public async Task BannerStack_TwoReadySessions_RendersTwoBanners()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);

        var banners = doc.QuerySelectorAll(".today-banner");
        Assert.Equal(2, banners.Length);
    }

    [Fact(DisplayName = "BannerStack — both banners carry the intake kind modifier class")]
    public async Task BannerStack_TwoBanners_BothHaveIntakeClass()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);

        var intakeBanners = doc.QuerySelectorAll(".today-banner--intake");
        Assert.Equal(2, intakeBanners.Length);
    }

    [Fact(DisplayName = "BannerStack — each banner has its own Review link")]
    public async Task BannerStack_EachBannerHasReviewLink()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);

        var reviewLinks = doc.QuerySelectorAll(".today-banner__review");
        Assert.Equal(2, reviewLinks.Length);
        // Both must point to distinct URLs
        var hrefs = reviewLinks.Select(l => l.GetAttribute("href")).Distinct().ToList();
        Assert.Equal(2, hrefs.Count);
    }
}

/// <summary>
/// L4 fragment tests for the review-banner stack — none-pending variant.
/// </summary>
public sealed class ReviewBannerStackNoSessionTests(TodayReviewBannerNoneFactory factory)
    : IClassFixture<TodayReviewBannerNoneFactory>
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

    [Fact(DisplayName = "BannerStack — no pending sessions renders no banner chrome at all")]
    public async Task BannerStack_NoPendingSessions_NoBannerChromeRendered()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);

        // No stack wrapper, no individual banners
        Assert.Null(doc.QuerySelector(".today-banner-stack"));
        Assert.Empty(doc.QuerySelectorAll(".today-banner"));
    }

    [Fact(DisplayName = "BannerStack — Today page renders normally (not cold-start) when no banners")]
    public async Task BannerStack_NoPendingSessions_TodayPageRendersNormally()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);

        // The today-wrap is present (page rendered normally, not redirected or error)
        Assert.NotNull(doc.QuerySelector(".today-wrap"));
        // The two-column grid is visible (not cold-start)
        Assert.NotNull(doc.QuerySelector(".today-grid"));
    }
}
