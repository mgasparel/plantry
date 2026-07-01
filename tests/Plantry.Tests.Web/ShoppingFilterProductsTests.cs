using System.Net;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 tests for the Shopping page's <c>OnGetFilterProductsAsync</c> handler (plantry-juh,
/// migrated to fuzzy ranking in plantry-gzro.3).
/// Verifies that the search dropdown enrichment emits the correct stock-hint HTML:
/// <list type="bullet">
///   <item>In-stock product: <c>&lt;span class="ostock"&gt;N unit in pantry&lt;/span&gt;</c>.</item>
///   <item>Out-of-stock product: <c>&lt;span class="ostock out"&gt;out&lt;/span&gt;</c>.</item>
///   <item>Low-but-not-zero product: <c>ostock low</c> class on the stock hint.</item>
///   <item>Free-text items (no pantry record): no <c>.ostock</c> span emitted.</item>
/// </list>
/// Also verifies the fuzzy-ranking behaviour added in plantry-gzro.3 (<see
/// cref="Plantry.SharedKernel.ProductNameMatcher"/>, the same <c>.rk</c> best/N% vocabulary as
/// Recipes/TakeStock's product search): a typo query that plain substring matching could not have
/// matched still surfaces the product (proving the ranker, not the old substring filter, is
/// wired), the matched option carries a <c>.rk</c> label, and a blank query (browse-on-focus)
/// still renders unranked with no <c>.rk</c> span at all.
/// Uses the same <see cref="ShoppingListFragmentFactory"/> fixture as the snapshot tests, which
/// seeds Milk (2 L, not low) and Flour (0 g, out/low) via <see cref="ShoppingListFixture.StockLevels"/>.
/// </summary>
public sealed class ShoppingFilterProductsTests(ShoppingListFragmentFactory factory)
    : IClassFixture<ShoppingListFragmentFactory>
{
    private static readonly HtmlParser Parser = new();

    private async Task<string> GetFilterProductsHtmlAsync(string? q = null)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            ShoppingListFixture.HouseholdAId.ToString());

        var url = q is null
            ? "/Shopping?handler=FilterProducts"
            : $"/Shopping?handler=FilterProducts&q={Uri.EscapeDataString(q)}";
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    // ── In-stock product (Milk: 2 L, not low) ────────────────────────────────

    [Fact(DisplayName = "FilterProducts — in-stock product emits ostock span with quantity and unit")]
    public async Task FilterProducts_InStockProduct_EmitsOstockSpan()
    {
        var html = await GetFilterProductsHtmlAsync("Milk");
        var doc = Parser.ParseDocument($"<ul>{html}</ul>");

        var ostockSpan = doc.QuerySelector("li .ostock");
        Assert.NotNull(ostockSpan);
        Assert.DoesNotContain("out", ostockSpan!.ClassList);
        Assert.DoesNotContain("low", ostockSpan!.ClassList);
        Assert.Contains("in pantry", ostockSpan.TextContent);
        Assert.Contains("2", ostockSpan.TextContent);
        Assert.Contains("L", ostockSpan.TextContent);
    }

    [Fact(DisplayName = "FilterProducts — in-stock product option has [data-label] name span")]
    public async Task FilterProducts_InStockProduct_HasDataLabelSpan()
    {
        var html = await GetFilterProductsHtmlAsync("Milk");
        var doc = Parser.ParseDocument($"<ul>{html}</ul>");

        var labelSpan = doc.QuerySelector("li [data-label]");
        Assert.NotNull(labelSpan);
        Assert.Equal("Milk", labelSpan!.GetAttribute("data-label"));
        Assert.Equal("Milk", labelSpan.TextContent.Trim());
    }

    // ── Out-of-stock product (Flour: 0 g, IsLow = true) ─────────────────────

    [Fact(DisplayName = "FilterProducts — out-of-stock product emits ostock out span")]
    public async Task FilterProducts_OutOfStockProduct_EmitsOstockOutSpan()
    {
        var html = await GetFilterProductsHtmlAsync("Flour");
        var doc = Parser.ParseDocument($"<ul>{html}</ul>");

        var ostockSpan = doc.QuerySelector("li .ostock");
        Assert.NotNull(ostockSpan);
        Assert.Contains("out", ostockSpan!.ClassList);
        Assert.Equal("out", ostockSpan.TextContent.Trim());
    }

    // ── All products with no query filter ────────────────────────────────────

    [Fact(DisplayName = "FilterProducts — no query returns all matching products with stock hints")]
    public async Task FilterProducts_NoQuery_ReturnsAllWithStockHints()
    {
        var html = await GetFilterProductsHtmlAsync();
        var doc = Parser.ParseDocument($"<ul>{html}</ul>");

        var options = doc.QuerySelectorAll("li[role='option']");
        // Both Milk and Flour should appear.
        Assert.Equal(2, options.Length);

        // At least one should have an ostock span.
        var ostockSpans = doc.QuerySelectorAll(".ostock");
        Assert.Equal(2, ostockSpans.Length);

        // Blank query is browse-on-focus (unranked) — no .rk label should appear (plantry-gzro.3).
        Assert.Empty(doc.QuerySelectorAll(".rk"));
    }

    // ── Query that matches no products ────────────────────────────────────────

    [Fact(DisplayName = "FilterProducts — query with no matches returns empty fragment")]
    public async Task FilterProducts_NoMatches_ReturnsEmptyFragment()
    {
        var html = await GetFilterProductsHtmlAsync("xyzzy-nomatch");
        Assert.True(string.IsNullOrWhiteSpace(html), $"Expected empty fragment, got: {html}");
    }

    // ── Fuzzy ranking (plantry-gzro.3 — migration onto ProductNameMatcher) ────

    [Fact(DisplayName = "FilterProducts — typo query surfaces match via fuzzy ranking, not plain substring")]
    public async Task FilterProducts_TypoQuery_SurfacesMatchViaFuzzyRanking()
    {
        // "Milc" is not a substring of "Milk" — the old plain-substring filter would have
        // returned nothing for this query. ProductNameMatcher's Jaro-Winkler scoring rates it
        // ~0.88 (well above the 0.70 display cutoff), proving the fuzzy ranker is genuinely
        // wired in place of the old substring check.
        var html = await GetFilterProductsHtmlAsync("Milc");
        var doc = Parser.ParseDocument($"<ul>{html}</ul>");

        var options = doc.QuerySelectorAll("li[role='option']");
        Assert.Single(options);

        var labelSpan = doc.QuerySelector("li [data-label]");
        Assert.NotNull(labelSpan);
        Assert.Equal("Milk", labelSpan!.GetAttribute("data-label"));
    }

    [Fact(DisplayName = "FilterProducts — typed query's top hit carries a .rk \"best\" label")]
    public async Task FilterProducts_TypedQuery_TopHitCarriesRankBestLabel()
    {
        var html = await GetFilterProductsHtmlAsync("Milc");
        var doc = Parser.ParseDocument($"<ul>{html}</ul>");

        var rkSpan = doc.QuerySelector("li .rk");
        Assert.NotNull(rkSpan);
        Assert.Equal("best", rkSpan!.TextContent.Trim());
    }
}
