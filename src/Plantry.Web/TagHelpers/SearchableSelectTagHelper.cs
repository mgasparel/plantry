using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Plantry.Web.TagHelpers;

/// <summary>
/// A type-to-filter replacement for &lt;select&gt; over long option lists. Renders a hidden input
/// (the value actually posted, bound via asp-for) plus a text input and popover listbox; Alpine
/// owns the open/close/keyboard-highlight state (see wwwroot/js/searchable-select.js) and htmx
/// swaps the &lt;li&gt; options as the user types when <see cref="SearchUrl"/> is set.
///
/// Option click handlers read the chosen value/label from the clicked element's own
/// data-value/textContent ($el in Alpine) rather than being interpolated into the emitted
/// JS expression — interpolating arbitrary option text into an inline handler would require
/// JS-string escaping that HTML attribute-encoding alone cannot provide safely.
/// </summary>
[HtmlTargetElement("searchable-select", Attributes = ForAttributeName)]
public sealed class SearchableSelectTagHelper(IHtmlGenerator htmlGenerator) : TagHelper
{
    private const string ForAttributeName = "asp-for";

    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = default!;

    [HtmlAttributeName(ForAttributeName)]
    public ModelExpression For { get; set; } = default!;

    /// <summary>Options rendered up front (selection state determines the initial display label) and as the htmx fallback target.</summary>
    public IEnumerable<SelectListItem> Items { get; set; } = [];

    /// <summary>htmx endpoint returning replacement &lt;li role="option"&gt; markup as the user types. Omit for client-rendered-only options.</summary>
    public string? SearchUrl { get; set; }

    public string? Placeholder { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var enc = HtmlEncoder.Default;

        var fullName = ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(For.Name);
        var hiddenId = TagBuilder.CreateSanitizedId(fullName, htmlGenerator.IdAttributeDotReplacement);
        var listboxId = $"{hiddenId}-listbox";

        var selected = Items.FirstOrDefault(i => i.Selected);
        var initialLabel = selected?.Text ?? "";
        var initialValue = selected?.Value ?? For.Model?.ToString() ?? "";

        var html = new StringBuilder();
        // `query` is seeded from data-initial-label by searchableSelect().init() (searchable-select.js),
        // so the x-data only needs to name the component.
        html.Append($"""<div class="searchable-select" x-data="searchableSelect()" data-initial-label="{enc.Encode(initialLabel)}" @click.outside="open = false" @keydown.escape="open = false">""");
        html.Append($"""<input type="hidden" name="{enc.Encode(fullName)}" id="{hiddenId}" value="{enc.Encode(initialValue)}" x-ref="hidden" />""");
        html.Append("""<div class="searchable-select__control">""");
        html.Append($"""<input type="text" class="field__input" role="combobox" aria-controls="{listboxId}" aria-autocomplete="list" autocomplete="off" placeholder="{enc.Encode(Placeholder ?? "Search…")}" x-model="query" :aria-expanded="open" @focus="open = true" @keydown.down.prevent="highlightNext()" @keydown.up.prevent="highlightPrev()" @keydown.enter.prevent="chooseHighlighted()" """);
        if (!string.IsNullOrWhiteSpace(SearchUrl))
        {
            html.Append($"""name="q" hx-get="{enc.Encode(SearchUrl)}" hx-trigger="keyup changed delay:250ms, focus" hx-target="#{listboxId}" hx-swap="innerHTML" hx-params="q" """);
        }
        html.Append("/>");
        html.Append("</div>");
        html.Append($"""<ul class="searchable-select__listbox" id="{listboxId}" role="listbox" x-ref="listbox" x-show="open" x-cloak>""");
        AppendOptions(html, Items, enc);
        html.Append("</ul>");
        html.Append("</div>");

        output.TagName = null;
        output.Content.SetHtmlContent(html.ToString());
    }

    internal static void AppendOptions(StringBuilder html, IEnumerable<SelectListItem> items, HtmlEncoder enc)
    {
        foreach (var item in items)
        {
            html.Append($"""<li role="option" data-value="{enc.Encode(item.Value ?? "")}" @click="select($el.dataset.value, $el.textContent.trim())">{enc.Encode(item.Text)}</li>""");
        }
    }
}
