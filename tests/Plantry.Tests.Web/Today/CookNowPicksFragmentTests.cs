using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Today;

/// <summary>
/// L4 fragment tests for the cook-now picks band on the Today page (SPEC Page 0 §0c, plantry-81g).
/// Each test fetches the real Today page as the fixture household (backed by in-memory fakes),
/// extracts a fragment, and verifies it for the correct content and structure.
///
/// Fixture recipe ordering (after SelectCookNowPicks):
///   1. Pasta Carbonara — expiring=true, pct=100 → "Use it up" badge, "Ready to cook" hint, has photo.
///   2. Veggie Stir     — expiring=false, pct=50  → "1 to pick up first" hint, placeholder photo.
///   3. Smoothie Bowl   — expiring=false, pct=0   → "2 to pick up first" hint, placeholder photo, cook time.
///
/// Tests cover:
/// <list type="bullet">
///   <item>Cookable pick: "Ready to cook" hint, no shopping badge.</item>
///   <item>Needs-shopping pick: "N to pick up first" hint.</item>
///   <item>Expiring badge: "Use it up" badge on expiring recipe.</item>
///   <item>Cook entry point: each pick has a Cook link to /Recipes/{id}/Cook.</item>
///   <item>Empty state: recipe-free household shows the empty prompt.</item>
/// </list>
/// </summary>
public sealed class CookNowPicksFragmentTests(TodayCookNowFragmentFactory factory)
    : IClassFixture<TodayCookNowFragmentFactory>
{
    private static readonly HtmlParser Parser = new();

    private async Task<string> GetTodayPageAsync()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            TodayCookNowFixture.HouseholdAId.ToString());
        var response = await client.GetAsync("/Today");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    // ── Section header ────────────────────────────────────────────────────────

    [Fact(DisplayName = "CookNow — section header reads 'What can I cook right now?'")]
    public async Task CookNow_SectionHeader_IsCorrect()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);
        var header = doc.QuerySelector("#today-meals-band .today-section-head h2");
        Assert.NotNull(header);
        Assert.Equal("What can I cook right now?", header.TextContent.Trim());
    }

    [Fact(DisplayName = "CookNow — section header has 'Browse recipes' link")]
    public async Task CookNow_SectionHeader_HasBrowseLink()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);
        var link = doc.QuerySelector("#today-meals-band .today-section-head a[href='/Recipes']");
        Assert.NotNull(link);
        Assert.Contains("Browse recipes", link.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    // ── Pick cards ────────────────────────────────────────────────────────────

    [Fact(DisplayName = "CookNow — renders 3 pick cards for fixture with 3 recipes")]
    public async Task CookNow_RendersThreePickCards()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);
        var cards = doc.QuerySelectorAll(".today-pick");
        Assert.Equal(3, cards.Length);
    }

    [Fact(DisplayName = "CookNow — cookable pick shows 'Ready to cook' hint")]
    public async Task CookNow_CookablePick_ShowsReadyHint()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);
        // Pasta Carbonara is fully cookable — should have ready hint
        var readyHints = doc.QuerySelectorAll(".today-pick__hint--ready");
        Assert.NotEmpty(readyHints);
        Assert.Contains(readyHints, h => h.TextContent.Contains("Ready to cook", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(DisplayName = "CookNow — needs-shopping pick shows 'to pick up first' hint")]
    public async Task CookNow_NeedsShoppingPick_ShowsShoppingHint()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);
        // Veggie Stir and Smoothie Bowl are not fully cookable — should have shopping hints
        var shopHints = doc.QuerySelectorAll(".today-pick__hint--shop");
        Assert.NotEmpty(shopHints);
        Assert.All(shopHints, h => Assert.Contains("to pick up first", h.TextContent, StringComparison.OrdinalIgnoreCase));
    }

    // ── Expiring badge ────────────────────────────────────────────────────────

    [Fact(DisplayName = "CookNow — expiring recipe shows 'Use it up' badge")]
    public async Task CookNow_ExpiringPick_ShowsUseItUpBadge()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);
        var badges = doc.QuerySelectorAll(".today-pick__badge--soon");
        Assert.NotEmpty(badges);
        Assert.All(badges, b => Assert.Contains("Use it up", b.TextContent, StringComparison.OrdinalIgnoreCase));
    }

    [Fact(DisplayName = "CookNow — non-expiring picks do not show 'Use it up' badge")]
    public async Task CookNow_NonExpiringPick_NoUseItUpBadge()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);
        // Only one recipe (Pasta Carbonara) has expiring ingredient
        var badges = doc.QuerySelectorAll(".today-pick__badge--soon");
        Assert.Single(badges);
    }

    // ── Cook entry point ──────────────────────────────────────────────────────

    [Fact(DisplayName = "CookNow — each pick has a Cook link to the recipe's Cook URL")]
    public async Task CookNow_EachPick_HasCookLink()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);
        var cookLinks = doc.QuerySelectorAll(".today-pick__cook");
        // Each pick must have a Cook link
        Assert.Equal(3, cookLinks.Length);
        Assert.All(cookLinks, link =>
        {
            var href = link.GetAttribute("href") ?? "";
            Assert.Matches(@"/Recipes/[0-9a-f-]+/Cook", href);
        });
    }

    // ── Photo ─────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "CookNow — recipe with photo renders an img element with correct handler=Photo src")]
    public async Task CookNow_RecipeWithPhoto_RendersImg()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);
        // Pasta Carbonara has a photo → should have an img in its pick with a ?handler=Photo src
        var imgs = doc.QuerySelectorAll(".today-pick__photo .today-pick__img");
        Assert.NotEmpty(imgs);
        Assert.All(imgs, img =>
        {
            var src = img.GetAttribute("src") ?? "";
            Assert.Contains("handler=Photo", src, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact(DisplayName = "CookNow — recipe without photo renders placeholder")]
    public async Task CookNow_RecipeWithoutPhoto_RendersPlaceholder()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);
        var placeholders = doc.QuerySelectorAll(".today-pick__photo--placeholder");
        Assert.NotEmpty(placeholders);
    }
}

/// <summary>
/// L4 fragment test for the empty-state of the cook-now picks band (no recipes in household).
/// </summary>
public sealed class CookNowPicksEmptyStateTests(TodayCookNowEmptyFactory factory)
    : IClassFixture<TodayCookNowEmptyFactory>
{
    private static readonly HtmlParser Parser = new();

    private async Task<string> GetTodayPageAsync()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            TodayCookNowFixture.HouseholdAId.ToString());
        var response = await client.GetAsync("/Today");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact(DisplayName = "CookNow — empty state shows prompt to add/browse recipes when no recipes exist")]
    public async Task CookNow_NoRecipes_ShowsEmptyPrompt()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);
        var emptyDiv = doc.QuerySelector(".today-meals__empty");
        Assert.NotNull(emptyDiv);
        Assert.Contains("recipe", emptyDiv.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "CookNow — empty state includes 'Add a recipe' action link")]
    public async Task CookNow_NoRecipes_HasAddRecipeLink()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);
        var addLink = doc.QuerySelector(".today-meals__empty a[href='/Recipes/New']");
        Assert.NotNull(addLink);
    }

    [Fact(DisplayName = "CookNow — empty state has no pick cards")]
    public async Task CookNow_NoRecipes_NoPickCards()
    {
        var html = await GetTodayPageAsync();
        var doc = Parser.ParseDocument(html);
        var cards = doc.QuerySelectorAll(".today-pick");
        Assert.Empty(cards);
    }
}
