using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Plantry.Web.TagHelpers;

/// <summary>
/// Renders the one shared page-header primitive (plantry-7yf1): the prominent title strip at the
/// top of every top-level page and the two product-detail pages. Supplies a required
/// <c>title</c> plus an optional context <c>eyebrow</c> pill, an optional <c>subtitle</c>, and an
/// optional muted <c>title-meta</c> suffix (e.g. "(archived)"). Any child content is rendered as
/// right-aligned header actions (buttons, an htmx trigger, a summary partial) — the reason this is
/// a tag helper rather than a partial is that a partial cannot slot arbitrary action markup, the
/// same rationale documented for <c>.card</c> in the Dev component library.
///
/// The subtitle can carry a <c>subtitle-id</c> so an htmx out-of-band swap can keep it live —
/// Pantry product detail passes <c>subtitle-id="product-total"</c> and its <c>_StockDetail</c>
/// OOB partial re-emits a matching <c>&lt;p id="product-total" class="page-header__subtitle"&gt;</c>.
///
/// An optional <c>cross-link-text</c> (with an optional <c>cross-link-href</c>) renders a small
/// <c>.xlink</c> affordance under the title inside <c>__main</c> (plantry-kkeg): the catalog↔pantry
/// cross-navigation between the two views of one product. With an href it is a live link; without
/// one it renders as muted, non-interactive text (the "never stocked → Not in pantry yet" case).
///
/// All text attributes are HTML-encoded, so dynamic values (product names, category names) are
/// safe to pass straight through.
/// </summary>
[HtmlTargetElement("page-header", Attributes = TitleAttributeName)]
public sealed class PageHeaderTagHelper : TagHelper
{
    private const string TitleAttributeName = "title";

    /// <summary>The page title (required) — rendered as the <c>h1.page-header__title</c>.</summary>
    [HtmlAttributeName(TitleAttributeName)]
    public string Title { get; set; } = default!;

    /// <summary>Optional context/section label rendered as an accent pill above the title.</summary>
    public string? Eyebrow { get; set; }

    /// <summary>Optional muted suffix rendered inline after the title (e.g. "(archived)").</summary>
    public string? TitleMeta { get; set; }

    /// <summary>Optional supporting sentence rendered under the title.</summary>
    public string? Subtitle { get; set; }

    /// <summary>Optional id on the subtitle element so an htmx OOB swap can target it.</summary>
    public string? SubtitleId { get; set; }

    /// <summary>
    /// Optional cross-link label rendered as a small <c>.xlink</c> under the title (plantry-kkeg).
    /// When <see cref="CrossLinkHref"/> is also set it is a live link; when the href is absent it
    /// renders as muted, non-interactive text (the "Not in pantry yet" hint).
    /// </summary>
    public string? CrossLinkText { get; set; }

    /// <summary>Optional destination for the <see cref="CrossLinkText"/> cross-link.</summary>
    public string? CrossLinkHref { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var actions = await output.GetChildContentAsync();
        var e = HtmlEncoder.Default;

        output.TagName = "header";
        // Force a full start+end tag: most call sites use the self-closing form
        // (<page-header ... />), which would otherwise render as an empty <header /> and drop
        // all of the title/subtitle content below.
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "page-header");

        var sb = new StringBuilder();
        sb.Append("""<div class="page-header__main">""");

        if (!string.IsNullOrWhiteSpace(Eyebrow))
        {
            sb.Append($"""<span class="page-header__eyebrow">{e.Encode(Eyebrow)}</span>""");
        }

        sb.Append("""<h1 class="page-header__title">""").Append(e.Encode(Title));
        if (!string.IsNullOrWhiteSpace(TitleMeta))
        {
            sb.Append($""" <span class="page-header__title-meta">{e.Encode(TitleMeta)}</span>""");
        }
        sb.Append("</h1>");

        if (!string.IsNullOrWhiteSpace(Subtitle))
        {
            var idAttr = string.IsNullOrWhiteSpace(SubtitleId)
                ? string.Empty
                : $" id=\"{e.Encode(SubtitleId)}\"";
            sb.Append($"""<p{idAttr} class="page-header__subtitle">{e.Encode(Subtitle)}</p>""");
        }

        if (!string.IsNullOrWhiteSpace(CrossLinkText))
        {
            sb.Append("""<div class="page-header__cross-link">""");
            if (!string.IsNullOrWhiteSpace(CrossLinkHref))
            {
                sb.Append($"""<a class="xlink" href="{e.Encode(CrossLinkHref)}">{e.Encode(CrossLinkText)}</a>""");
            }
            else
            {
                sb.Append($"""<span class="xlink xlink--muted">{e.Encode(CrossLinkText)}</span>""");
            }
            sb.Append("</div>");
        }

        sb.Append("</div>");
        output.PreContent.SetHtmlContent(sb.ToString());

        // Render child content as the right-aligned actions region — but only when present,
        // so a header with no actions doesn't emit an empty flex column.
        if (!actions.IsEmptyOrWhiteSpace)
        {
            output.PreContent.AppendHtml("""<div class="page-header__actions">""");
            output.Content.SetHtmlContent(actions);
            output.PostContent.SetHtmlContent("</div>");
        }
    }
}
