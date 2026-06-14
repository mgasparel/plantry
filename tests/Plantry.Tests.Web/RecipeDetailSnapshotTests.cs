using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 fragment snapshot tests for the recipe Detail page. Each test fetches the real Detail page
/// as household A, extracts one fragment (the full detail container, hero, meta, ingredient list,
/// or directions), and verifies it against a committed baseline. Any unintended change to the
/// rendered markup fails the snapshot, ensuring the detail render stays stable as other slices land.
///
/// <para>The fixture recipe covers all render paths: a photo (→ img tag in hero), tag pills,
/// a grouped ingredient list with an untracked staple (C12), and multi-paragraph directions
/// including a section heading (C13 derivation).</para>
/// </summary>
public sealed class RecipeDetailSnapshotTests(RecipeDetailFragmentFactory factory)
    : IClassFixture<RecipeDetailFragmentFactory>
{
    private static readonly HtmlParser Parser = new();

    private async Task<string> GetDetailPageAsync()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeDetailFixture.HouseholdAId.ToString());
        var response = await client.GetAsync($"/Recipes/{factory.RecipeId}");
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

    // ── Hero: photo present → img link renders ────────────────────────────────

    [Fact]
    public async Task Detail_hero_with_photo()
    {
        var html = await GetDetailPageAsync();
        await Verify(Extract(html, ".recipe-hero"), "html");
    }

    // ── Meta: servings, cook time, source, tag pills ──────────────────────────

    [Fact]
    public async Task Detail_meta()
    {
        var html = await GetDetailPageAsync();
        await Verify(Extract(html, ".recipe-meta"), "html");
    }

    [Fact]
    public async Task Detail_tags()
    {
        var html = await GetDetailPageAsync();
        await Verify(Extract(html, ".recipe-tags"), "html");
    }

    // ── Ingredients: group headings + untracked staple ────────────────────────

    [Fact]
    public async Task Detail_ingredients()
    {
        var html = await GetDetailPageAsync();
        // The ingredient section is the first .catalog-section after the hero meta section.
        // We snap the full directions section identified by the recipe-directions container.
        var doc = Parser.ParseDocument(html);
        // Select the ingredient list(s): both .recipe-ingredient-list elements together.
        var lists = doc.QuerySelectorAll(".recipe-ingredient-list");
        var combined = string.Join("\n", lists.Select(el =>
        {
            using var w = new StringWriter();
            el.ToHtml(w, new PrettyMarkupFormatter());
            return w.ToString().Replace("\r\n", "\n").Trim();
        }));
        await Verify(combined, "html");
    }

    // ── Directions: steps and section headings (C13) ──────────────────────────

    [Fact]
    public async Task Detail_directions()
    {
        var html = await GetDetailPageAsync();
        await Verify(Extract(html, "#recipe-directions"), "html");
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
