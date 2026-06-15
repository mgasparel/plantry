using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 fragment snapshot tests for the Shopping page (P2-Sc, SPEC §3a-3e).
/// Each test fetches the real Shopping page as household B (backed by in-memory fakes),
/// extracts a fragment, and verifies it against a committed baseline.
///
/// <para>Fixture covers all grouping + checked-state render paths:</para>
/// <list type="bullet">
///   <item>Milk in "Dairy" category — unchecked.</item>
///   <item>Sriracha (free-text) in "Uncategorized" — unchecked.</item>
///   <item>Flour in "Baking" category — checked, sinks to bottom.</item>
/// </list>
/// </summary>
public sealed class ShoppingListSnapshotTests(ShoppingListFragmentFactory factory)
    : IClassFixture<ShoppingListFragmentFactory>
{
    private static readonly HtmlParser Parser = new();

    private async Task<string> GetShoppingPageAsync()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            ShoppingListFixture.HouseholdAId.ToString());
        var response = await client.GetAsync("/Shopping");
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

    private static IEnumerable<string> ExtractAll(string pageHtml, string selector)
    {
        var doc = Parser.ParseDocument(pageHtml);
        return doc.QuerySelectorAll(selector)
            .Select(e =>
            {
                using var w = new StringWriter();
                e.ToHtml(w, new PrettyMarkupFormatter());
                return w.ToString().Replace("\r\n", "\n").Trim();
            });
    }

    // ── Full list container (groups + uncategorized + checked at bottom) ──────

    [Fact]
    public async Task Shopping_list_grouped()
    {
        var html = await GetShoppingPageAsync();
        await Verify(Extract(html, "#shopping-list"), "html");
    }

    // ── Uncategorized bucket contains the free-text item ─────────────────────

    [Fact]
    public async Task Shopping_uncategorized_bucket()
    {
        var html = await GetShoppingPageAsync();
        // The Uncategorized section heading should appear and contain the free-text item.
        var groups = ExtractAll(html, ".shopping-group").ToList();
        // Verify only the "Uncategorized" group heading section
        var uncategorized = groups.FirstOrDefault(g => g.Contains("Uncategorized"))
            ?? throw new InvalidOperationException("No Uncategorized group found.");
        await Verify(uncategorized, "html");
    }

    // ── Checked item renders with struck-through style class ──────────────────

    [Fact]
    public async Task Shopping_checked_item_has_checked_class()
    {
        var html = await GetShoppingPageAsync();
        var doc = Parser.ParseDocument(html);
        var checkedItems = doc.QuerySelectorAll(".shopping-item--checked");
        // There should be exactly one checked item (Flour).
        Assert.Single(checkedItems);
        var checkedItem = checkedItems.Single();
        using var writer = new StringWriter();
        checkedItem.ToHtml(writer, new PrettyMarkupFormatter());
        await Verify(writer.ToString().Replace("\r\n", "\n").Trim(), "html");
    }

    // ── Clear-checked button renders when there are checked items ─────────────

    [Fact]
    public async Task Shopping_clear_checked_button_visible()
    {
        var html = await GetShoppingPageAsync();
        var doc = Parser.ParseDocument(html);
        var actions = doc.QuerySelector(".shopping-list__actions");
        Assert.NotNull(actions);
        using var writer = new StringWriter();
        actions!.ToHtml(writer, new PrettyMarkupFormatter());
        await Verify(writer.ToString().Replace("\r\n", "\n").Trim(), "html");
    }

    // ── Unauthenticated request is challenged ─────────────────────────────────

    [Fact]
    public async Task Shopping_unauthenticated_is_challenged()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var response = await client.GetAsync("/Shopping");
        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized,
            $"Expected redirect or 401, got {(int)response.StatusCode}");
    }
}
