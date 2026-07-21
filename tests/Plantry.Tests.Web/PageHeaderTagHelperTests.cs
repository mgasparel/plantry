using System.Text;
using System.Text.Encodings.Web;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Plantry.Web.TagHelpers;

namespace Plantry.Tests.Web;

/// <summary>
/// Unit tests for the shared <see cref="PageHeaderTagHelper"/>'s catalog ⇄ pantry cross-link slot
/// (plantry-kkeg): a <c>.xlink</c> rendered under the title inside <c>page-header__main</c>. With a
/// href it is a live link; without one it is muted, non-interactive text (the "never stocked →
/// Not in pantry yet" case). The pre-existing title/eyebrow/subtitle/actions behaviour is exercised
/// by the pages that consume the primitive and their E2E journeys.
/// </summary>
public sealed class PageHeaderTagHelperTests
{
    private static readonly HtmlParser Parser = new();

    /// <summary>
    /// Invokes <see cref="PageHeaderTagHelper.ProcessAsync"/> and reconstructs the full element
    /// (the helper emits the title strip into <c>PreContent</c> and any actions into
    /// <c>Content</c>/<c>PostContent</c>) so it can be parsed as HTML.
    /// </summary>
    private static async Task<string> RenderAsync(PageHeaderTagHelper helper)
    {
        var context = new TagHelperContext(
            allAttributes: new TagHelperAttributeList(),
            items: new Dictionary<object, object>(),
            uniqueId: "test");

        var output = new TagHelperOutput(
            "page-header",
            attributes: new TagHelperAttributeList(),
            getChildContentAsync: (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

        await helper.ProcessAsync(context, output);

        var sb = new StringBuilder();
        sb.Append('<').Append(output.TagName);
        foreach (var attr in output.Attributes)
        {
            sb.Append(' ').Append(attr.Name).Append("=\"").Append(attr.Value).Append('"');
        }
        sb.Append('>');
        sb.Append(output.PreContent.GetContent(HtmlEncoder.Default));
        sb.Append(output.Content.GetContent(HtmlEncoder.Default));
        sb.Append(output.PostContent.GetContent(HtmlEncoder.Default));
        sb.Append("</").Append(output.TagName).Append('>');
        return sb.ToString();
    }

    [Fact(DisplayName = "cross-link-text + cross-link-href renders a live .xlink anchor under the title")]
    public async Task CrossLinkWithHref_RendersLiveAnchor()
    {
        var helper = new PageHeaderTagHelper
        {
            Title = "Whole milk",
            CrossLinkText = "View in pantry →",
            CrossLinkHref = "/Pantry/Products/Detail/abc",
        };

        var doc = Parser.ParseDocument(await RenderAsync(helper));

        var link = doc.QuerySelector(".page-header__main a.xlink");
        Assert.NotNull(link);
        Assert.Equal("/Pantry/Products/Detail/abc", link!.GetAttribute("href"));
        Assert.Equal("View in pantry →", link.TextContent);
        // Live link must not carry the muted (non-interactive) modifier.
        Assert.DoesNotContain("xlink--muted", link.ClassList);
    }

    [Fact(DisplayName = "cross-link-text without a href renders muted, non-interactive text (no anchor)")]
    public async Task CrossLinkWithoutHref_RendersMutedText()
    {
        var helper = new PageHeaderTagHelper
        {
            Title = "Saffron threads",
            CrossLinkText = "Not in pantry yet",
        };

        var html = await RenderAsync(helper);
        var doc = Parser.ParseDocument(html);

        // No dead link: the muted case is a <span>, never an anchor.
        Assert.Null(doc.QuerySelector("a.xlink"));
        var muted = doc.QuerySelector(".page-header__main span.xlink.xlink--muted");
        Assert.NotNull(muted);
        Assert.Equal("Not in pantry yet", muted!.TextContent);
    }

    [Fact(DisplayName = "no cross-link-text renders no cross-link markup at all")]
    public async Task NoCrossLink_RendersNothing()
    {
        var helper = new PageHeaderTagHelper { Title = "Deals" };

        var doc = Parser.ParseDocument(await RenderAsync(helper));

        Assert.Null(doc.QuerySelector(".page-header__cross-link"));
        Assert.Null(doc.QuerySelector(".xlink"));
    }

    [Fact(DisplayName = "cross-link href and text are HTML-encoded (raw-markup breakout payload)")]
    public async Task CrossLink_IsHtmlEncoded()
    {
        var helper = new PageHeaderTagHelper
        {
            Title = "Whole milk",
            // Breakout payloads that would inject markup / break out of the href attribute if
            // either value were emitted unencoded.
            CrossLinkText = "<b>x</b>",
            CrossLinkHref = "/p?a=1&b=2\"><script>alert(1)</script>",
        };

        // Assert on the RAW rendered markup (not an AngleSharp-decoded attribute, which would hide
        // a missing encode): a dropped e.Encode on either value must turn this red.
        var html = await RenderAsync(helper);

        Assert.DoesNotContain("<script>", html);          // no injected live markup
        Assert.DoesNotContain("\"><script", html);         // href did not break out of its attribute
        Assert.Contains("&lt;b&gt;x&lt;/b&gt;", html);     // link text encoded (kills text-encode mutant)
        Assert.Contains("a=1&amp;b=2", html);              // href ampersand encoded
        Assert.Contains("&quot;&gt;&lt;script&gt;", html); // href quote/brackets encoded (kills href-encode mutant)
    }
}
