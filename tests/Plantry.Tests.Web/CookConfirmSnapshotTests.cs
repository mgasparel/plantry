using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 fragment snapshot tests for the Cook confirmation page (P2-3d, J4).
///
/// The fixture recipe (Garlic Pasta, 4 servings) exercises all Cook-page render paths:
///   • Pasta (leaf, InStock, no shortfall)
///   • Canned Tomatoes (leaf, shortfall — 200g available, 500g needed)
///   • Garlic (parent product, Variant Disambiguation Picker with two options:
///       — Garlic, Fresh: compatible, 5 ea available, auto-selected as best
///       — Garlic, Granule: unit-incompatible (tbsp vs ea), visible but disabled)
///   • Salt (untracked staple: shown greyed, no quantity)
///
/// Each test fetches the Cook page via the WAF (no DB), extracts one fragment, and verifies
/// it against a committed baseline. To seed initial baselines: PLANTRY_ACCEPT_SNAPSHOTS=1 dotnet test
/// </summary>
public sealed class CookConfirmSnapshotTests(CookConfirmFragmentFactory factory)
    : IClassFixture<CookConfirmFragmentFactory>
{
    private static readonly HtmlParser Parser = new();

    private async Task<string> GetCookPageAsync()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            CookConfirmFixture.HouseholdAId.ToString());
        // Request at default servings (4).
        var response = await client.GetAsync($"/Recipes/{factory.RecipeId}/Cook?Servings=4");
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

    // ── Full cook-confirm container ────────────────────────────────────────────

    [Fact]
    public async Task Cook_full()
    {
        var html = await GetCookPageAsync();
        await Verify(Extract(html, "#cook-confirm"), "html");
    }

    // ── Cook header: recipe name + servings + scale badge ─────────────────────

    [Fact]
    public async Task Cook_header()
    {
        var html = await GetCookPageAsync();
        await Verify(Extract(html, ".cook-header"), "html");
    }

    // ── Shortfall notice appears when any line is short ───────────────────────

    [Fact]
    public async Task Cook_shortfall_notice_present()
    {
        var html = await GetCookPageAsync();
        // Tomatoes has shortfall (200g available, 500g needed) — notice must render.
        Assert.Contains("cook-shortfall-notice", html, StringComparison.Ordinal);
    }

    // ── Ingredient list (the card body) ───────────────────────────────────────

    [Fact]
    public async Task Cook_ingredient_list()
    {
        var html = await GetCookPageAsync();
        await Verify(Extract(html, ".cook-ing-list"), "html");
    }

    // ── Variant Disambiguation Picker: present for parent-product ingredient ──

    [Fact]
    public async Task Cook_variant_picker_rendered()
    {
        var html = await GetCookPageAsync();
        // A picker must be present (Garlic is a parent product).
        Assert.Contains("cook-picker", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cook_variant_picker_fresh_garlic_auto_selected()
    {
        var html = await GetCookPageAsync();
        // "Garlic, Fresh" should be marked as the best auto-selected variant.
        Assert.Contains("Garlic, Fresh", html, StringComparison.OrdinalIgnoreCase);
        // "best" label present in the picker.
        Assert.Contains("best", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cook_variant_picker_incompatible_variant_present_but_disabled()
    {
        var html = await GetCookPageAsync();
        // "Garlic, Granule" must appear even though it is unit-incompatible.
        Assert.Contains("Garlic, Granule", html, StringComparison.OrdinalIgnoreCase);
        // The incompatibility label must be present (renders the "unit mismatch" badge).
        Assert.Contains("unit mismatch", html, StringComparison.OrdinalIgnoreCase);
    }

    // ── Shortfall tag on a low-stock line ─────────────────────────────────────

    [Fact]
    public async Task Cook_shortfall_tag_on_tomato_line()
    {
        var html = await GetCookPageAsync();
        // Tomatoes line shows the shortfall tag with "have … / need …" text.
        Assert.Contains("cook-shortfall-tag", html, StringComparison.Ordinal);
        // The available amount (200g) should appear.
        Assert.Contains("200", html, StringComparison.Ordinal);
    }

    // ── Untracked staple renders greyed with "to taste" ───────────────────────

    [Fact]
    public async Task Cook_untracked_staple_rendered()
    {
        var html = await GetCookPageAsync();
        Assert.Contains("to taste", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Salt", html, StringComparison.OrdinalIgnoreCase);
    }

    // ── Action rail is present ────────────────────────────────────────────────

    [Fact]
    public async Task Cook_action_rail()
    {
        var html = await GetCookPageAsync();
        await Verify(Extract(html, ".cook-rail"), "html");
    }

    // ── Unauthenticated request is challenged ──────────────────────────────────

    [Fact]
    public async Task Cook_unauthenticated_is_challenged()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var response = await client.GetAsync($"/Recipes/{factory.RecipeId}/Cook");
        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized,
            $"Expected redirect or 401, got {(int)response.StatusCode}");
    }

    // ── Foreign household cannot access the Cook page ─────────────────────────

    [Fact]
    public async Task Cook_foreign_household_returns_notfound()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            "cccccccc-0000-0000-0000-000000000003");
        var response = await client.GetAsync($"/Recipes/{factory.RecipeId}/Cook");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
