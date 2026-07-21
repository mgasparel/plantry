using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Catalog;

/// <summary>
/// L4 Web integration tests for the catalog → pantry cross-link on the Product Detail page
/// (plantry-kkeg). Reuses <see cref="ProductDetailAddVariantFactory"/>, which already seeds a
/// product that holds stock (has a <c>ProductStock</c> record) and one that never has — the exact
/// distinction that decides whether the cross-link is a live "View in pantry" link or the muted
/// "Not in pantry yet" hint.
/// </summary>
public sealed class ProductDetailCrossLinkTests : IDisposable
{
    private static readonly HtmlParser Parser = new();
    private readonly ProductDetailAddVariantFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient AuthClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, ProductDetailAddVariantFixture.HouseholdId.ToString());
        return client;
    }

    // AC: from catalog detail, a visible "View in pantry" affordance navigates to that product's
    //     pantry detail when stock exists.
    [Fact(DisplayName = "Catalog detail — product with stock renders a live 'View in pantry' link to its pantry detail")]
    public async Task WithStock_RendersLivePantryLink()
    {
        var client = AuthClient();
        var productId = ProductDetailAddVariantFixture.ProductWithStockId;

        var html = await (await client.GetAsync($"/Catalog/Products/{productId}")).Content.ReadAsStringAsync();
        var doc = Parser.ParseDocument(html);

        var link = doc.QuerySelector("a.xlink");
        Assert.NotNull(link);
        Assert.Equal($"/Pantry/Products/Detail/{productId}", link!.GetAttribute("href"));
        Assert.Contains("View in pantry", link.TextContent);
        // Live link — not the muted hint.
        Assert.Null(doc.QuerySelector("span.xlink--muted"));
    }

    // AC: when the catalog product has never been stocked, the page shows muted "Not in pantry yet"
    //     text instead of a link (no 404, no dead link).
    [Fact(DisplayName = "Catalog detail — never-stocked product shows muted 'Not in pantry yet' text, no link")]
    public async Task NeverStocked_RendersMutedHintNotALink()
    {
        var client = AuthClient();
        var productId = ProductDetailAddVariantFixture.ProductNoStockId;

        var html = await (await client.GetAsync($"/Catalog/Products/{productId}")).Content.ReadAsStringAsync();
        var doc = Parser.ParseDocument(html);

        var muted = doc.QuerySelector("span.xlink.xlink--muted");
        Assert.NotNull(muted);
        Assert.Equal("Not in pantry yet", muted!.TextContent);

        // No dead link and no 404 target: there must be no anchor cross-link at all.
        Assert.Null(doc.QuerySelector("a.xlink"));
        Assert.DoesNotContain($"/Pantry/Products/Detail/{productId}", html);
    }
}
