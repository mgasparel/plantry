using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Plantry.Tests.Web.Dev;

/// <summary>
/// Guards against the recurring /Dev component-gallery duplicate-id bug class (plantry-4gft; third
/// recorded occurrence after plantry-7p32 and plantry-s9rh): a gallery section that reuses a bound demo
/// input model — or otherwise hard-codes an id also used elsewhere on the page — renders colliding
/// id/for attributes, which breaks label association and any id-scoped JS/CSS hook. This is the one
/// guard that pins the class page-wide; a future per-instance dup-id finding on /Dev is resolved by
/// this test catching it, not by a new bead.
/// </summary>
public sealed class DevGalleryPageTests
{
    [Fact]
    public async Task Development_Gallery_Renders_With_No_Duplicate_Ids()
    {
        await using var factory = new DevEnvironmentFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = true });

        var response = await client.GetAsync("/Dev");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        var doc = new HtmlParser().ParseDocument(html);

        var allIds = doc.QuerySelectorAll("[id]");

        // Landmark assertion: proves the gallery actually rendered its sections rather than passing
        // vacuously on a near-empty body (e.g. a future refactor that renders nothing) — the dup-id
        // guard below is meaningless unless the page it inspects genuinely loaded.
        Assert.NotNull(doc.QuerySelector("#products-grid"));
        Assert.True(allIds.Length >= 10,
            $"Gallery appears not to have rendered — only {allIds.Length} element(s) with an id.");

        var idCounts = allIds
            .Select(el => el.Id!)
            .GroupBy(id => id)
            .Where(g => g.Count() > 1)
            .ToList();

        var failureMessage = string.Join(", ", idCounts.Select(g => $"'{g.Key}' x{g.Count()}"));
        Assert.True(idCounts.Count == 0, $"Duplicate id values found on /Dev: {failureMessage}");
    }

    [Fact]
    public async Task DataGrid_Caption_Is_VisuallyHidden_Only_When_Set()
    {
        await using var factory = new DevEnvironmentFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = true });

        var response = await client.GetAsync("/Dev");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        var doc = new HtmlParser().ParseDocument(html);

        // CaptionVisuallyHidden: true (Utilities/.sr-only demo grid) — caption renders with sr-only.
        var hidden = doc.QuerySelector("#utility-caption-demo-grid caption");
        Assert.NotNull(hidden);
        Assert.Contains("data-grid__caption", hidden!.ClassList);
        Assert.Contains("sr-only", hidden.ClassList);

        // CaptionVisuallyHidden left unset (default false, Data grid demo) — caption renders visible.
        var visible = doc.QuerySelector("#products-grid caption");
        Assert.NotNull(visible);
        Assert.Contains("data-grid__caption", visible!.ClassList);
        Assert.DoesNotContain("sr-only", visible.ClassList);
    }
}
