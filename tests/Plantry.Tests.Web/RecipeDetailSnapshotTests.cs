using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 fragment snapshot tests for the recipe Detail page. Each test fetches the real Detail page
/// as household A, extracts one fragment (the full detail container, hero, meta strip, ingredient card,
/// or directions), and verifies it against a committed baseline. Any unintended change to the
/// rendered markup fails the snapshot, ensuring the detail render stays stable as other slices land.
///
/// <para>The fixture recipe covers all render paths: a photo (→ img tag in hero), tag pills,
/// a grouped ingredient list with an untracked staple (C12), and multi-paragraph directions
/// including a section heading (C13 derivation).</para>
///
/// <para>Layout: the Detail page uses the two-column rd-grid layout (plantry-v0f). Selectors
/// reference the new class names: .rd-hero, .rd-meta, .rd-fulf-card, .rd-ing-card, .rd-hero__tags,
/// #recipe-directions.</para>
/// </summary>
public sealed class RecipeDetailSnapshotTests(
    RecipeDetailFragmentFactory factory,
    RecipeDetailFullCostFactory fullCostFactory,
    RecipeDetailNoCostFactory noCostFactory)
    : IClassFixture<RecipeDetailFragmentFactory>,
      IClassFixture<RecipeDetailFullCostFactory>,
      IClassFixture<RecipeDetailNoCostFactory>
{
    private static readonly HtmlParser Parser = new();

    private Task<string> GetDetailPageAsync() => GetDetailPageAsync(factory);

    private static async Task<string> GetDetailPageAsync(RecipeDetailFragmentFactory f)
    {
        var client = f.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeDetailFixture.HouseholdAId.ToString());
        var response = await client.GetAsync($"/Recipes/{f.RecipeId}");
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

    // ── Full detail container ─────────────────────────────────────────────────

    [Fact]
    public async Task Detail_full()
    {
        var html = await GetDetailPageAsync();
        await Verify(Extract(html, "#recipe-detail"), "html");
    }

    // ── Hero: photo present → img link renders with overlaid info ────────────

    [Fact]
    public async Task Detail_hero_with_photo()
    {
        var html = await GetDetailPageAsync();
        await Verify(Extract(html, ".rd-hero"), "html");
    }

    // ── Meta stat strip: cook time, servings, cost ────────────────────────────

    [Fact]
    public async Task Detail_meta()
    {
        var html = await GetDetailPageAsync();
        await Verify(Extract(html, ".rd-meta"), "html");
    }

    // ── Tags: rendered in the hero info overlay ───────────────────────────────

    [Fact]
    public async Task Detail_tags()
    {
        var html = await GetDetailPageAsync();
        await Verify(Extract(html, ".rd-hero__tags"), "html");
    }

    // ── Ingredients: rd-ing-card with stepper + grouped rows ─────────────────

    [Fact]
    public async Task Detail_ingredients()
    {
        var html = await GetDetailPageAsync();
        await Verify(Extract(html, ".rd-ing-card"), "html");
    }

    // ── Directions: steps and section headings (C13) ──────────────────────────

    [Fact]
    public async Task Detail_directions()
    {
        var html = await GetDetailPageAsync();
        await Verify(Extract(html, "#recipe-directions"), "html");
    }

    // ── Fulfillment rail card: mixed statuses (InStock / Low / Missing) ───────

    [Fact]
    public async Task Detail_fulfillment_card_mixed_statuses()
    {
        var html = await GetDetailPageAsync();
        await Verify(Extract(html, ".rd-fulf-card"), "html");
    }

    // ── Cost: shown in meta strip when Partial ────────────────────────────────

    [Fact]
    public async Task Detail_cost_bar_partial()
    {
        var html = await GetDetailPageAsync();
        // The cost figure lives in the third cell of the meta strip.
        // Select the cell by its mono-value class.
        var doc = Parser.ParseDocument(html);
        var cell = doc.QuerySelector(".rd-meta__val--mono")?.ParentElement?.ParentElement
            ?? throw new InvalidOperationException("'.rd-meta__val--mono' not found in page HTML.");
        using var writer = new StringWriter();
        cell.ToHtml(writer, new AngleSharp.Html.PrettyMarkupFormatter());
        await Verify(writer.ToString().Replace("\r\n", "\n").Trim(), "html");
    }

    // ── Cost Partial: mono value IS present when completeness is Partial ─────

    [Fact]
    public async Task Detail_cost_bar_present_when_partial()
    {
        // The default factory has Partial prices. This test asserts the mono cost value
        // IS present and the partial-estimate marker is rendered in the meta strip.
        var html = await GetDetailPageAsync(factory);
        Assert.Contains("rd-meta__val--mono", html, StringComparison.Ordinal);
        Assert.Contains("Partial cost estimate", html, StringComparison.Ordinal);
    }

    // ── Cost Full: mono value present, no partial-estimate "~" marker ────────

    [Fact]
    public async Task Detail_cost_full_omits_estimate_marker()
    {
        // Every costable ingredient priced → CostCompleteness.Full. The mono cost value renders,
        // but the "~" partial-estimate marker (title="Partial cost estimate …") must NOT appear.
        var html = await GetDetailPageAsync(fullCostFactory);
        Assert.Contains("rd-meta__val--mono", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Partial cost estimate", html, StringComparison.Ordinal);
    }

    // ── Cost currency: a non-USD (EUR) household renders the € symbol via MoneyDisplay ──────────

    [Fact(DisplayName = "Detail cost meta renders the € symbol for a EUR household (plantry-2x6e.2)")]
    public async Task Detail_cost_uses_household_display_currency()
    {
        using var eurFactory = new RecipeDetailEurCostFactory();
        var html = await GetDetailPageAsync(eurFactory);

        var doc = Parser.ParseDocument(html);
        // The whole meta strip: both the per-serving cost cell and the "· €x.xx total" label render through
        // MoneyDisplay with the household's EUR currency, so the strip carries '€' and never a hardcoded '$'.
        var metaStrip = doc.QuerySelector(".rd-meta")?.TextContent
            ?? throw new InvalidOperationException("'.rd-meta' not found in page HTML.");

        Assert.Contains("€", metaStrip, StringComparison.Ordinal);
        Assert.DoesNotContain("$", metaStrip, StringComparison.Ordinal);
    }

    // ── Cost None: dash cell, no mono cost value (J3 — never shown as zero) ───

    [Fact]
    public async Task Detail_cost_none_shows_dash_not_value()
    {
        // No ingredient priced → CostCompleteness.None. The meta strip renders the faint dash
        // placeholder and omits the mono cost value and the per-serving/total label.
        var html = await GetDetailPageAsync(noCostFactory);
        Assert.DoesNotContain("rd-meta__val--mono", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Partial cost estimate", html, StringComparison.Ordinal);

        // The cost cell (third meta cell) shows the em-dash placeholder under the "Cost per serving" label.
        var doc = Parser.ParseDocument(html);
        var costLabel = doc.QuerySelectorAll(".rd-meta__lbl")
            .FirstOrDefault(e => e.TextContent.Trim() == "Cost per serving")
            ?? throw new InvalidOperationException("'Cost per serving' meta cell not found.");
        var cell = costLabel.ParentElement!;
        Assert.Contains("—", cell.TextContent);
    }

    // ── Unauthenticated request is challenged (401 in test env, 302 in prod) ───

    [Fact]
    public async Task Detail_unauthenticated_is_challenged()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        // No household header → TestAuthHandler returns NoResult → [Authorize] challenges.
        // In the Testing environment there is no redirect to /Account/Login configured
        // (that is the Identity cookie handler, which is replaced), so the response is 401.
        var response = await client.GetAsync($"/Recipes/{factory.RecipeId}");
        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized,
            $"Expected redirect or 401, got {(int)response.StatusCode}");
    }

    // ── Foreign household cannot read the recipe ──────────────────────────────

    [Fact]
    public async Task Detail_foreign_household_returns_notfound()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        // Household B has a different id — the fake repository will not return the recipe.
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            "bbbbbbbb-0000-0000-0000-000000000002");
        var response = await client.GetAsync($"/Recipes/{factory.RecipeId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
