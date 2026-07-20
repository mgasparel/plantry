using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 fragment snapshot tests for the recipe Browse page (P2-2c, J1/J2).
/// Each test fetches the real Browse page as household A (backed by in-memory fakes), extracts a
/// fragment, and verifies it against a committed baseline. Unintended markup changes fail the test.
///
/// <para>The fixture covers all browse render paths (P2-2c acceptance criteria):</para>
/// <list type="bullet">
///   <item>Pancakes — Vegetarian tag, fully in-stock, no expiry → Cook-tonight flag, known cost.</item>
///   <item>Omelette — Spicy tag, in-stock, eggs expiring in 2 days → Use-soon badge, known cost.</item>
///   <item>Milk Shake — no tag, in-stock, no price data → cost cell omitted.</item>
/// </list>
/// </summary>
public sealed class RecipeBrowseSnapshotTests(RecipeBrowseFragmentFactory factory)
    : IClassFixture<RecipeBrowseFragmentFactory>
{
    private static readonly HtmlParser Parser = new();

    private async Task<string> GetBrowsePageAsync(string? query = null)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeBrowseFixture.HouseholdAId.ToString());
        var url = query is null ? "/Recipes" : $"/Recipes?{query}";
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static string Extract(string pageHtml, string selector)
    {
        var doc = Parser.ParseDocument(pageHtml);
        var element = doc.QuerySelector(selector)
            ?? throw new InvalidOperationException($"Selector '{selector}' not found in page HTML.");
        using var writer = new StringWriter();
        element.ToHtml(writer, new PrettyMarkupFormatter());
        return writer.ToString().Replace("\r\n", "\n").Trim();
    }

    // ── Full results container (gallery mode default) ─────────────────────────

    [Fact]
    public async Task Browse_gallery_results()
    {
        var html = await GetBrowsePageAsync();
        await Verify(Extract(html, "#recipes-gallery"), "html");
    }

    // ── Grid mode: trigger with sort=name to get a deterministic stable order ─

    [Fact]
    public async Task Browse_grid_results()
    {
        // x-show on #recipes-grid is Alpine-controlled; we verify the raw markup.
        var html = await GetBrowsePageAsync("sort=name&desc=false");
        await Verify(Extract(html, "#recipes-grid"), "html");
    }

    // ── Use-soon badge renders on the "soon" recipe (Omelette) ───────────────

    [Fact]
    public async Task Browse_gallery_use_soon_badge()
    {
        // The gallery renders Use-soon flag only on Omelette (eggs expiring in 2 days).
        var html = await GetBrowsePageAsync();
        var doc = Parser.ParseDocument(html);
        var flags = doc.QuerySelectorAll(".recipe-card__flag--soon");
        await Verify(string.Join("\n\n", flags.Select(f =>
        {
            using var w = new StringWriter();
            f.ToHtml(w, new PrettyMarkupFormatter());
            return w.ToString().Replace("\r\n", "\n").Trim();
        })), "html");
    }

    // ── NoCost recipe omits cost in gallery and grid ──────────────────────────

    [Fact]
    public async Task Browse_grid_omits_cost_when_none()
    {
        var html = await GetBrowsePageAsync("sort=name&desc=false");
        // Grid row for Milk Shake should have a dash in the cost column, not a price.
        var doc = Parser.ParseDocument(html);
        // Select all cost cells in grid rows
        var costCells = doc.QuerySelectorAll(".recipes-grid__row .recipes-grid__cell--cost");
        await Verify(string.Join("\n\n", costCells.Select(c =>
        {
            using var w = new StringWriter();
            c.ToHtml(w, new PrettyMarkupFormatter());
            return w.ToString().Replace("\r\n", "\n").Trim();
        })), "html");
    }

    // ── Cost currency: a non-USD (EUR) household renders '€' cost cells via MoneyDisplay ──────────

    [Fact(DisplayName = "Browse cost cells render the € symbol for a EUR household (plantry-2x6e.2)")]
    public async Task Browse_cost_uses_household_display_currency()
    {
        using var eurFactory = new RecipeBrowseEurFactory();
        var client = eurFactory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeBrowseFixture.HouseholdAId.ToString());
        var response = await client.GetAsync("/Recipes?sort=name&desc=false");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        var doc = Parser.ParseDocument(html);
        // The priced grid rows (Pancakes, Omelette) render their cost-per-serving through MoneyDisplay with the
        // household's EUR currency; concatenate the cost cells and assert the '€' symbol, never a hardcoded '$'.
        var costText = string.Concat(
            doc.QuerySelectorAll(".recipes-grid__row .recipes-grid__cell--cost").Select(c => c.TextContent));
        Assert.Contains("€", costText, StringComparison.Ordinal);
        Assert.DoesNotContain("$", costText, StringComparison.Ordinal);
    }

    // ── Toolbar: tag filter chips ─────────────────────────────────────────────

    [Fact]
    public async Task Browse_tag_filter_chips()
    {
        var html = await GetBrowsePageAsync();
        await Verify(Extract(html, ".filter-chip-bar"), "html");
    }

    // ── htmx partial swap: HX-Request returns bare fragment, not full layout ───

    [Fact]
    public async Task Browse_htmx_request_returns_partial_fragment_not_full_page()
    {
        // Simulates a filter/sort htmx request: sends HX-Request header, expects
        // only the results fragment — NOT the full page with <html>/<body>/layout chrome.
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeBrowseFixture.HouseholdAId.ToString());
        client.DefaultRequestHeaders.Add("HX-Request", "true");

        var response = await client.GetAsync("/Recipes?sort=name&desc=false");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();

        // The partial must NOT contain full-page layout elements.
        Assert.DoesNotContain("<html", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<body", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recipes-browse-head", html, StringComparison.OrdinalIgnoreCase);

        // The partial MUST contain the results region content.
        Assert.True(
            html.Contains("recipes-gallery", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("recipes-grid", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("empty-state", StringComparison.OrdinalIgnoreCase),
            "Response should contain gallery, grid, or empty-state element.");
    }

    // ── Photo thumbnail renders with ?handler=Photo URL (regression guard for HasPhoto branch) ──

    [Fact]
    public async Task Browse_gallery_photo_img_uses_handler_url()
    {
        // Pancakes fixture recipe has a photo set; the gallery must render an <img> whose
        // src uses the ?handler=Photo query-string convention (not the /Photo path form which
        // does not route to the Razor Page handler and causes a 404).
        var html = await GetBrowsePageAsync();
        var doc = Parser.ParseDocument(html);
        var img = doc.QuerySelector(".recipe-card__photo img")
            ?? throw new InvalidOperationException("Expected a recipe-card photo <img> in gallery (Pancakes fixture has a photo).");
        var src = img.GetAttribute("src") ?? string.Empty;
        Assert.Contains("?handler=Photo", src, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/Photo", src.Split('?')[0], StringComparison.OrdinalIgnoreCase);
    }

    // ── Unauthenticated request is challenged ─────────────────────────────────

    [Fact]
    public async Task Browse_unauthenticated_is_challenged()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var response = await client.GetAsync("/Recipes");
        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized,
            $"Expected redirect or 401, got {(int)response.StatusCode}");
    }
}
