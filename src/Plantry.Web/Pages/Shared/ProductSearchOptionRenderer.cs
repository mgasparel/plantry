using System.Text;
using System.Text.Encodings.Web;

namespace Plantry.Web.Pages.Shared;

/// <summary>
/// The ONE canonical renderer for product-search <c>&lt;li role="option"&gt;</c> markup returned by
/// the app's htmx search endpoints (plantry-su0y). Six page handlers previously hand-built this
/// interpolated markup and drifted in their <c>data-*</c> attribute and <c>$dispatch</c> payload
/// names; this class is now the single source of that markup, so the attribute/payload contract
/// cannot silently diverge from the consumers that read it — <c>wwwroot/js/searchable-select.js</c>
/// queries <c>[role="option"]</c>, and <c>_ProductSearchCreateSheet</c> plus the inline-add rows
/// listen for the <c>pick-product</c> event.
///
/// <para>Two interaction variants, matching the two protocols in the app:</para>
/// <list type="bullet">
///   <item><description><b>Variant A — <see cref="RenderSelectOption"/></b>: the SearchableSelect
///   <c>select()</c> protocol (Shopping add-item, the Dev fuzzy demo). The option carries its display
///   name in a <c>[data-label]</c> span and the click handler writes the chosen value/label into the
///   component's hidden input.</description></item>
///   <item><description><b>Variant B — <see cref="RenderPickProductOption"/></b>: the
///   <c>pick-product</c> dispatch protocol (Deals correction, Take Stock inline-add, Recipes
///   Cook/Edit). The click handler seeds <c>query</c>, closes the popover, and dispatches a
///   <c>pick-product</c> CustomEvent whose payload keys are built from the same
///   <see cref="ProductOptionField"/> list as the <c>data-*</c> attributes, so the two cannot
///   drift.</description></item>
/// </list>
///
/// <para>Every dynamic value is HTML-encoded with <see cref="HtmlEncoder.Default"/> in both attribute
/// and text positions (Guids pass through unchanged). The Alpine <c>@click</c> expressions never
/// begin with <c>var</c> (bd memory <c>alpine-event-handler-var-token-syntax-error</c>).</para>
/// </summary>
public static class ProductSearchOptionRenderer
{
    /// <summary>
    /// Variant A — one <c>&lt;li role="option"&gt;</c> for the SearchableSelect <c>select()</c> protocol.
    /// Emits the value in <c>data-value</c>, the display name in a <c>[data-label]</c> span (so the click
    /// handler prefers it over <c>textContent</c>, which would otherwise include the <c>.rk</c> label),
    /// an optional <c>.rk</c> rank span, and any host-specific trailing body HTML.
    /// </summary>
    /// <param name="value">Option value written to <c>data-value</c> (a ProductId, or the name itself for
    /// the id-less Dev demo). HTML-encoded.</param>
    /// <param name="label">Display name, rendered in both the <c>[data-label]</c> attribute and the visible
    /// text. HTML-encoded.</param>
    /// <param name="rankLabel">Optional <see cref="Plantry.SharedKernel.ProductNameMatcher"/> rank label
    /// (best / N%). When null (e.g. an unranked browse-on-focus list) no <c>.rk</c> span is emitted.</param>
    /// <param name="extraBodyHtml">Optional pre-built, already-encoded HTML appended inside the
    /// <c>&lt;li&gt;</c> after the rank span. Used for host-specific enrichment such as Shopping's
    /// <c>.ostock</c> pantry-stock badge; the caller owns encoding of this fragment.</param>
    public static string RenderSelectOption(
        string value,
        string label,
        string? rankLabel = null,
        string? extraBodyHtml = null)
    {
        var enc = HtmlEncoder.Default;
        var html = new StringBuilder();
        html.Append($"""<li role="option" data-value="{enc.Encode(value)}" @click="select($el.dataset.value, $el.querySelector('[data-label]')?.dataset.label ?? $el.textContent.trim())"><span data-label="{enc.Encode(label)}">{enc.Encode(label)}</span>""");
        if (rankLabel is not null)
            html.Append($"""<span class="rk">{enc.Encode(rankLabel)}</span>""");
        if (extraBodyHtml is not null)
            html.Append(extraBodyHtml);
        html.Append("</li>");
        return html.ToString();
    }

    /// <summary>
    /// Variant B — one <c>&lt;li role="option"&gt;</c> for the <c>pick-product</c> dispatch protocol.
    /// The click handler seeds <c>query</c> from <c>data-name</c>, closes the popover, and dispatches a
    /// bubbling <c>pick-product</c> event. Its payload always carries <c>value</c> and <c>name</c>
    /// (read from <c>data-value</c>/<c>data-name</c>); each entry in <paramref name="extraFields"/>
    /// contributes one <c>data-*</c> attribute and, when it names a <see cref="ProductOptionField.PayloadKey"/>,
    /// one payload key — both from the same source so they cannot drift.
    /// </summary>
    /// <param name="value">Product id written to <c>data-value</c>. HTML-encoded.</param>
    /// <param name="name">Product name, rendered in <c>data-name</c> and as the visible text. HTML-encoded.</param>
    /// <param name="rankLabel"><see cref="Plantry.SharedKernel.ProductNameMatcher"/> rank label
    /// (best / N%), always rendered as a trailing <c>.rk</c> span. HTML-encoded.</param>
    /// <param name="extraFields">Per-host extra <c>data-*</c> attributes and their payload wiring
    /// (track / defaultUnit / defaultUnitCode / defaultLocation). Order is preserved in both the emitted
    /// attributes and the payload. Null or empty emits just <c>value</c> and <c>name</c>.</param>
    public static string RenderPickProductOption(
        string value,
        string name,
        string rankLabel,
        IReadOnlyList<ProductOptionField>? extraFields = null)
    {
        var enc = HtmlEncoder.Default;

        var attrs = new StringBuilder();
        var payload = new StringBuilder();
        if (extraFields is not null)
        {
            foreach (var field in extraFields)
            {
                attrs.Append(" data-").Append(field.Attribute).Append("=\"")
                     .Append(enc.Encode(field.Value)).Append('"');
                if (field.PayloadKey is not null)
                {
                    var expr = field.PayloadExpression ?? $"$el.dataset.{ToDatasetKey(field.Attribute)}";
                    payload.Append($", {field.PayloadKey}: {expr}");
                }
            }
        }

        return $$"""<li role="option" data-value="{{enc.Encode(value)}}" data-name="{{enc.Encode(name)}}"{{attrs}} @click="query = $el.dataset.name; open = false; $dispatch('pick-product', {value: $el.dataset.value, name: $el.dataset.name{{payload}}})">{{enc.Encode(name)}}<span class="rk">{{enc.Encode(rankLabel)}}</span></li>""";
    }

    /// <summary>
    /// Converts a kebab-case <c>data-*</c> attribute suffix to the camelCase key the browser exposes on
    /// <c>element.dataset</c> (e.g. <c>default-unit-code</c> → <c>defaultUnitCode</c>). Used to derive the
    /// default <c>$el.dataset.*</c> payload expression from a field's attribute name.
    /// </summary>
    private static string ToDatasetKey(string attribute)
    {
        var parts = attribute.Split('-');
        var sb = new StringBuilder(parts[0]);
        for (var i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length == 0)
                continue;
            sb.Append(char.ToUpperInvariant(parts[i][0]));
            sb.Append(parts[i], 1, parts[i].Length - 1);
        }
        return sb.ToString();
    }
}

/// <summary>
/// One extra <c>data-*</c> attribute on a <c>pick-product</c> option (Variant B), and how it feeds the
/// <c>$dispatch('pick-product', …)</c> payload. See <see cref="ProductSearchOptionRenderer.RenderPickProductOption"/>.
/// </summary>
/// <param name="Attribute">The <c>data-*</c> suffix, kebab-case, e.g. <c>track</c>, <c>default-unit</c>,
/// <c>default-location</c>. Rendered as <c>data-{Attribute}</c>.</param>
/// <param name="Value">The raw attribute value; HTML-encoded into the attribute.</param>
/// <param name="PayloadKey">Optional key this field contributes to the dispatched payload, e.g.
/// <c>defaultUnitId</c>. When null the attribute is emitted but never read into the payload (Take Stock's
/// <c>data-default-location</c>, which is read elsewhere).</param>
/// <param name="PayloadExpression">Optional explicit JS expression for the payload value. When null it
/// defaults to <c>$el.dataset.{camelCase(Attribute)}</c> (read the attribute back off the element). Pass a
/// literal such as <c>'true'</c> to bake a constant into the payload (Deals / Take Stock's <c>track</c>).</param>
public sealed record ProductOptionField(
    string Attribute,
    string Value,
    string? PayloadKey = null,
    string? PayloadExpression = null);
