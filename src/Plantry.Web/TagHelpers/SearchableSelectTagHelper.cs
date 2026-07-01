using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Plantry.Web.TagHelpers;

/// <summary>
/// A type-to-filter replacement for &lt;select&gt; over long option lists, and (per <see
/// cref="AllowCreate"/>) the ONE shared fuzzy-ranked product search + create control (plantry-gzro).
/// Renders a text input and popover listbox; Alpine owns the open/close/keyboard-highlight state
/// (see wwwroot/js/searchable-select.js) and htmx swaps the &lt;li&gt; options as the user types
/// when <see cref="SearchUrl"/> is set.
///
/// <para><b>Bound vs. unbound (plantry-gzro.1):</b> <see cref="For"/> (asp-for) is optional. When
/// present, a hidden input carries the value that actually posts with the form (Shopping's
/// add-item search) and each option's default click handler calls the Alpine <c>select()</c>
/// method, which writes into it. When absent, there is no hidden input — the host renders its own
/// &lt;li&gt; markup (via its own htmx search handler) with a custom <c>@@click</c> that reads
/// <c>$el.dataset.*</c> and dispatches whatever event contract it needs (e.g. Recipes/TakeStock's
/// <c>pick-product</c>) — the same "host owns per-item enrichment, component owns chrome" pattern
/// Shopping's <c>OnGetFilterProductsAsync</c> already proves out for the bound case.</para>
///
/// <para><b>Fuzzy ranking</b> is entirely a host concern: the host's search handler ranks
/// candidates with <see cref="Plantry.SharedKernel.ProductNameMatcher"/> and emits a <c>.rk</c>
/// span per option (the <c>best</c>/<c>N%</c> vocabulary shared with Intake's AlternativesStrip
/// family) — this component only supplies the CSS to display it consistently
/// (<c>.searchable-select__listbox .rk</c>). There is no separate "fuzzy" flag; ranking is
/// standard whenever a host's search endpoint performs it.</para>
///
/// <para><b>AllowCreate</b> is the one parameter distinguishing the two supported modes: plain
/// ranked search (Shopping — no create option, it has its own free-text "Custom item" escape
/// hatch) vs. ranked search + create (Recipes/TakeStock). When true, a divider and a "+ Create
/// ..." button render directly below the listbox — demoted (<c>btn--demoted</c>) while the
/// listbox currently has matches, full-strength when it doesn't — matching
/// docs/Engineering/prototypes/fuzzy-match-suggestion-widget.html Option A. Clicking it dispatches
/// a bubbling <c>product-search-create</c> CustomEvent with <c>{ query }</c> for the host to
/// handle (e.g. switch to a create view, as <c>_ProductSearchCreateSheet</c> will on migration).</para>
///
/// Option click handlers read the chosen value/label from the clicked element's own
/// data-value/textContent ($el in Alpine) rather than being interpolated into the emitted
/// JS expression — interpolating arbitrary option text into an inline handler would require
/// JS-string escaping that HTML attribute-encoding alone cannot provide safely.
/// </summary>
[HtmlTargetElement("searchable-select")]
public sealed class SearchableSelectTagHelper(IHtmlGenerator htmlGenerator) : TagHelper
{
    private const string ForAttributeName = "asp-for";

    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = default!;

    /// <summary>
    /// Optional. When set, a hidden input (bound to this expression) carries the posted value and
    /// the default option click handler writes into it via Alpine's <c>select()</c>. Omit for the
    /// unbound event-dispatch pattern (host renders its own enriched &lt;li&gt; click handlers).
    /// </summary>
    [HtmlAttributeName(ForAttributeName)]
    public ModelExpression? For { get; set; }

    /// <summary>Options rendered up front (selection state determines the initial display label) and as the htmx fallback target.</summary>
    public IEnumerable<SelectListItem> Items { get; set; } = [];

    /// <summary>htmx endpoint returning replacement &lt;li role="option"&gt; markup as the user types. Omit for client-rendered-only options.</summary>
    public string? SearchUrl { get; set; }

    public string? Placeholder { get; set; }

    /// <summary>
    /// Opt-in "create new" escape hatch (plantry-gzro.1). When true, a divider + demoted/full-strength
    /// button render below the listbox (see the class doc). Defaults to false — plain ranked/filtered
    /// search only, e.g. Shopping which has its own free-text create affordance.
    /// </summary>
    public bool AllowCreate { get; set; } = false;

    /// <summary>
    /// Text shown after the quoted query in the create button, e.g. "+ Create "chicken" {CreateLabel}".
    /// Defaults to "as a new product". Only rendered when <see cref="AllowCreate"/> is true.
    /// </summary>
    public string CreateLabel { get; set; } = "as a new product";

    /// <summary>
    /// Optional explicit id for the unbound-mode combobox (plantry-gzro.2). When set, it replaces the
    /// randomly-generated <c>ss-&lt;guid&gt;</c> fallback as the base id the listbox id is derived from.
    /// Only meaningful when <see cref="For"/> is omitted — bound mode already derives a deterministic
    /// id from the field name. Hosts that render this component once per page inside markup covered by
    /// approval/snapshot tests (e.g. <c>_ProductSearchCreateSheet</c>) should set this so the emitted
    /// ids are stable across renders instead of a fresh GUID every time.
    /// </summary>
    public string? Id { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var enc = HtmlEncoder.Default;

        var hasFor = For is not null;
        var fullName = hasFor ? ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(For!.Name) : "";
        var hiddenId = hasFor
            ? TagBuilder.CreateSanitizedId(fullName, htmlGenerator.IdAttributeDotReplacement)
            : Id ?? "ss-" + Guid.NewGuid().ToString("N");
        var listboxId = $"{hiddenId}-listbox";

        var selected = Items.FirstOrDefault(i => i.Selected);
        var initialLabel = selected?.Text ?? "";
        var initialValue = selected?.Value ?? (hasFor ? For!.Model?.ToString() ?? "" : "");

        var html = new StringBuilder();
        // `query` is seeded from data-initial-label by searchableSelect().init() (searchable-select.js),
        // so the x-data only needs to name the component. data-create-label similarly seeds the
        // Alpine `createLabel` field used by the AllowCreate button's x-text below.
        html.Append($"""<div class="searchable-select" x-data="searchableSelect()" data-initial-label="{enc.Encode(initialLabel)}" data-create-label="{enc.Encode(CreateLabel)}" @click.outside="open = false" @keydown.escape="open = false" """);
        // Pass through any attributes the host wrote on <searchable-select> that don't map to a bound
        // property above (e.g. an Alpine `@sheet-product-set.window` listener that must live on this
        // same x-data scope to reach its `query`/`open`/`highlighted` fields — plantry-gzro.2). The
        // framework has already stripped recognized attributes (asp-for, items, search-url, ...) from
        // output.Attributes by this point, so only genuinely unrecognized ones remain.
        foreach (var attribute in output.Attributes)
        {
            html.Append(attribute.Name).Append("=\"").Append(enc.Encode(attribute.Value?.ToString() ?? "")).Append("\" ");
        }
        html.Append('>');
        if (hasFor)
        {
            html.Append($"""<input type="hidden" name="{enc.Encode(fullName)}" id="{hiddenId}" value="{enc.Encode(initialValue)}" x-ref="hidden" />""");
        }
        html.Append("""<div class="searchable-select__control">""");
        html.Append($"""<input type="text" class="field__input" role="combobox" aria-controls="{listboxId}" aria-autocomplete="list" autocomplete="off" placeholder="{enc.Encode(Placeholder ?? "Search…")}" x-model="query" :aria-expanded="open" @focus="open = true" @keydown.down.prevent="highlightNext()" @keydown.up.prevent="highlightPrev()" @keydown.enter.prevent="chooseHighlighted()" """);
        if (!string.IsNullOrWhiteSpace(SearchUrl))
        {
            html.Append($"""name="q" hx-get="{enc.Encode(SearchUrl)}" hx-trigger="keyup changed delay:250ms, focus" hx-target="#{listboxId}" hx-swap="innerHTML" hx-params="q" """);
        }
        html.Append("/>");
        html.Append("</div>");
        html.Append($"""<ul class="searchable-select__listbox" id="{listboxId}" role="listbox" x-ref="listbox" x-show="open" x-cloak style="display: none" """);
        if (AllowCreate)
        {
            // Drives the create button's demoted/full-strength state (see below) — independent of
            // `open` so "no matches" still reads correctly even while the popover is closed.
            html.Append("""@htmx:after-swap="hasMatches = $event.target.children.length > 0" """);
        }
        html.Append(">");
        AppendOptions(html, Items, enc);
        html.Append("</ul>");
        if (AllowCreate)
        {
            // Divider + create button sit directly below the listbox so the two read as one
            // visually grouped unit, matching the prototype's Option A (.lb-divider treatment).
            // Gated on `open` (same popover visibility as the listbox, so the pair never appears
            // detached while the popover itself is closed) AND a non-empty query (no "Create ''"
            // before the user has typed anything) — combining the prototype's query-driven gating
            // with the real app's existing open/close popover semantics.
            html.Append("""<hr class="searchable-select__create-divider" x-show="open && query.trim().length > 0" x-cloak />""");
            html.Append("""<button type="button" class="btn btn--ghost btn--sm" :class="{ 'btn--demoted': hasMatches }" x-show="open && query.trim().length > 0" x-cloak @click="$dispatch('product-search-create', { query: query })">""");
            html.Append("""<span x-text="'+ Create “' + query + '” ' + createLabel"></span>""");
            html.Append("</button>");
        }
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
