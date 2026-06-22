using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Plantry.Web.TagHelpers;

/// <summary>
/// The single canonical −/[value]/+ stepper primitive.
///
/// <para>
/// Two structural modes:
/// <list type="bullet">
///   <item><term>Input mode</term><description>renders an <c>&lt;input type="number"&gt;</c> that posts in a form.
///       Set <see cref="Name"/> (and optionally <see cref="AspFor"/> for model binding,
///       <see cref="Value"/> for server-side prefill, and <see cref="XModel"/> / <see cref="XModelModifier"/>
///       for Alpine x-model binding, or <see cref="ValueBind"/> / <see cref="InputHandler"/>
///       for one-way :value + @input binding).</description></item>
///   <item><term>Display mode</term><description>renders a <c>&lt;span&gt;</c> driven by Alpine (no form post).
///       Set <see cref="XText"/> for the Alpine expression; omit <see cref="Name"/>.</description></item>
/// </list>
/// </para>
///
/// <para>
/// Three visual variants (CSS modifier):
/// <list type="bullet">
///   <item><term>(default)</term><description>40 px rectangular icon buttons — intake qty, cook qty, shopping add-form, inline-edit qty.</description></item>
///   <item><term>compact</term><description>borderless square icon buttons inside a bordered container — recipe-detail ingredients header.</description></item>
///   <item><term>pill</term><description>24 px circular text-glyph (−/+) buttons, inline flow — recipe-meta strip servings.</description></item>
/// </list>
/// </para>
///
/// Usage examples:
/// <code>
/// // Input stepper (intake review drawer, default 40px size)
/// &lt;stepper name="Edit.Quantity" x-model="v"
///          decrease-click="v = Math.max(0.001, (parseFloat(v)||0) - 1)"
///          increase-click="v = (parseFloat(v)||0) + 1"
///          decrease-label="Decrease quantity" increase-label="Increase quantity" /&gt;
///
/// // Input stepper with .number modifier (shopping add-form, default 40px size)
/// &lt;stepper name="Input.Quantity" x-model="qty" x-model-modifier="number"
///          decrease-click="qty = Math.max(0, +(qty - 1).toFixed(2))"
///          increase-click="qty = +(qty + 1).toFixed(2)"
///          decrease-label="Decrease" increase-label="Increase" /&gt;
///
/// // Input stepper with one-way :value + @input (cook page per-ingredient qty)
/// &lt;stepper name="" value-bind="quantity('@id')" input-handler="setQuantity('@id', $event.target.value)"
///          input-disabled="isSkipped('@id')"
///          decrease-click="adjustQty('@id', -1)" decrease-disabled="isSkipped('@id')"
///          increase-click="adjustQty('@id', 1)"  increase-disabled="isSkipped('@id')"
///          decrease-label="Decrease" increase-label="Increase" /&gt;
///
/// // Display stepper, compact variant (recipe ingredients header)
/// &lt;stepper variant="compact" group-label="Servings" x-text="servings" display-fallback="4"
///          decrease-click="servings = Math.max(1, servings - 1)"
///          increase-click="servings = Math.min(24, servings + 1)"
///          decrease-disabled="servings &lt;= 1" increase-disabled="servings &gt;= 24"
///          decrease-label="Fewer servings" increase-label="More servings" /&gt;
///
/// // Display stepper, pill variant (recipe-meta strip)
/// &lt;stepper variant="pill" group-label="Servings"
///          x-text="servings + (servings === 1 ? ' serving' : ' servings')"
///          display-fallback="4 servings"
///          decrease-click="servings = Math.max(1, servings - 1)"
///          increase-click="servings = servings + 1"
///          decrease-disabled="servings &lt;= 1"
///          decrease-label="Fewer servings" increase-label="More servings" /&gt;
/// </code>
/// </summary>
[HtmlTargetElement("stepper")]
public sealed class StepperTagHelper : TagHelper
{
    // ── Mode ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The HTML <c>name</c> attribute for the number input (input mode).
    /// Omit for display mode. Takes precedence over <see cref="AspFor"/>-derived name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Model expression for <c>asp-for</c> model binding (input mode).
    /// Derives the <c>name</c> and <c>id</c> attributes when <see cref="Name"/> is not set.
    /// </summary>
    [HtmlAttributeName("asp-for")]
    public ModelExpression? AspFor { get; set; }

    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext? ViewContext { get; set; }

    // ── Alpine wiring ───────────────────────────────────────────────────────────

    /// <summary>
    /// Alpine model expression name (input mode only).
    /// Emitted as <c>x-model="…"</c> or <c>x-model.{modifier}="…"</c>.
    /// Mutually exclusive with <see cref="ValueBind"/>/<see cref="InputHandler"/>.
    /// Common values: <c>"v"</c>, <c>"qty"</c>.
    /// </summary>
    [HtmlAttributeName("x-model")]
    public string? XModel { get; set; }

    /// <summary>
    /// Optional Alpine model modifier (e.g. <c>"number"</c> → emits <c>x-model.number</c>).
    /// Only meaningful when <see cref="XModel"/> is set.
    /// </summary>
    [HtmlAttributeName("x-model-modifier")]
    public string? XModelModifier { get; set; }

    /// <summary>
    /// Alpine one-way binding expression for the input value (input mode only).
    /// Emitted as <c>:value="…"</c>. Use when the value is computed per-item
    /// (e.g. <c>quantity('id')</c>) rather than bound via x-model.
    /// Mutually exclusive with <see cref="XModel"/>.
    /// </summary>
    [HtmlAttributeName("value-bind")]
    public string? ValueBind { get; set; }

    /// <summary>
    /// Alpine <c>@input</c> event handler expression for the input element (input mode only).
    /// Used alongside <see cref="ValueBind"/> for per-item one-way binding.
    /// Example: <c>"setQuantity('id', $event.target.value)"</c>.
    /// </summary>
    [HtmlAttributeName("input-handler")]
    public string? InputHandler { get; set; }

    /// <summary>
    /// Alpine <c>:disabled</c> expression for the input element (input mode only).
    /// Used when the whole row can be skipped (e.g. cook page skip toggle).
    /// </summary>
    [HtmlAttributeName("input-disabled")]
    public string? InputDisabled { get; set; }

    /// <summary>Alpine <c>x-text</c> expression for the display span (display mode only).</summary>
    [HtmlAttributeName("x-text")]
    public string? XText { get; set; }

    /// <summary>Alpine <c>@click</c> expression for the Decrease button.</summary>
    [HtmlAttributeName("decrease-click")]
    public string DecreaseClick { get; set; } = "";

    /// <summary>Alpine <c>@click</c> expression for the Increase button.</summary>
    [HtmlAttributeName("increase-click")]
    public string IncreaseClick { get; set; } = "";

    /// <summary>Alpine <c>:disabled</c> expression for the Decrease button (optional).</summary>
    [HtmlAttributeName("decrease-disabled")]
    public string? DecreaseDisabled { get; set; }

    /// <summary>Alpine <c>:disabled</c> expression for the Increase button (optional).</summary>
    [HtmlAttributeName("increase-disabled")]
    public string? IncreaseDisabled { get; set; }

    // ── HTML attributes ─────────────────────────────────────────────────────────

    /// <summary><c>aria-label</c> for the Decrease button. Default: "Decrease".</summary>
    [HtmlAttributeName("decrease-label")]
    public string DecreaseLabel { get; set; } = "Decrease";

    /// <summary><c>aria-label</c> for the Increase button. Default: "Increase".</summary>
    [HtmlAttributeName("increase-label")]
    public string IncreaseLabel { get; set; } = "Increase";

    /// <summary>
    /// <c>aria-label</c> for the group wrapper. Omitted from the DOM when null.
    /// </summary>
    [HtmlAttributeName("group-label")]
    public string? GroupLabel { get; set; }

    /// <summary>HTML <c>id</c> for the number input (input mode only). Derived from <see cref="AspFor"/> when omitted.</summary>
    [HtmlAttributeName("input-id")]
    public string? InputId { get; set; }

    /// <summary>
    /// <c>aria-label</c> for the number input element (input mode only).
    /// Required when the input has no associated <c>&lt;label&gt;</c> in the surrounding markup.
    /// </summary>
    [HtmlAttributeName("input-label")]
    public string? InputLabel { get; set; }

    /// <summary>
    /// Fallback text rendered inside the display span before Alpine hydrates (display mode only).
    /// </summary>
    [HtmlAttributeName("display-fallback")]
    public string? DisplayFallback { get; set; }

    /// <summary>
    /// Server-side prefill value for the number input <c>value</c> attribute (input mode only).
    /// </summary>
    [HtmlAttributeName("value")]
    public string? Value { get; set; }

    // ── Visual variant ──────────────────────────────────────────────────────────

    /// <summary>
    /// Visual variant. Accepted values: <c>"default"</c> (or omit), <c>"compact"</c>, <c>"pill"</c>.
    /// </summary>
    public string Variant { get; set; } = "default";

    // ───────────────────────────────────────────────────────────────────────────

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = null; // suppress the <stepper> element itself

        var e = HtmlEncoder.Default;

        // Resolve input name from asp-for when not explicitly set.
        var inputName = Name
            ?? (AspFor is not null && ViewContext is not null
                ? ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(AspFor.Name)
                : null);

        var isInput = inputName is not null;
        var isPill  = Variant is "pill";

        var wrapperClass = Variant switch
        {
            "compact" => "stepper stepper--compact",
            "pill"    => "stepper stepper--pill",
            _         => "stepper",
        };

        var html = new StringBuilder();

        // ── Wrapper ────────────────────────────────────────────────────────────
        html.Append($"<div class=\"{e.Encode(wrapperClass)}\" role=\"group\"");
        if (GroupLabel is not null)
            html.Append($""" aria-label="{e.Encode(GroupLabel)}" """);
        html.Append('>');

        // ── Decrease button ────────────────────────────────────────────────────
        html.Append($"""<button type="button" class="stepper__btn" aria-label="{e.Encode(DecreaseLabel)}" @click="{e.Encode(DecreaseClick)}" """);
        if (DecreaseDisabled is not null)
            html.Append($""":disabled="{e.Encode(DecreaseDisabled)}" """);
        html.Append('>');
        if (isPill)
            html.Append("−");
        else
            html.Append("""<svg class="icon" aria-hidden="true"><use href="#i-minus" /></svg>""");
        html.Append("</button>");

        // ── Value slot ─────────────────────────────────────────────────────────
        if (isInput)
        {
            // Resolve id from asp-for when not explicitly set.
            var inputId = InputId
                ?? (AspFor is not null && ViewContext is not null
                    ? TagBuilder.CreateSanitizedId(
                        ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(AspFor.Name),
                        ".")
                    : null);

            html.Append("""<input class="stepper__val" type="number" """);
            html.Append($"""name="{e.Encode(inputName!)}" """);
            if (inputId is not null)
                html.Append($"""id="{e.Encode(inputId)}" """);
            html.Append("""min="0" step="any" """);
            if (InputLabel is not null)
                html.Append($"""aria-label="{e.Encode(InputLabel)}" """);
            if (Value is not null)
                html.Append($"""value="{e.Encode(Value)}" """);

            if (XModel is not null)
            {
                // x-model mode: two-way Alpine binding.
                var xModelAttr = XModelModifier is not null
                    ? $"x-model.{XModelModifier}"
                    : "x-model";
                html.Append($"""{e.Encode(xModelAttr)}="{e.Encode(XModel)}" """);
            }
            else if (ValueBind is not null)
            {
                // One-way binding mode: :value + @input + optional :disabled on input.
                html.Append($""":value="{e.Encode(ValueBind)}" """);
                if (InputHandler is not null)
                    html.Append($"""@input="{e.Encode(InputHandler)}" """);
                if (InputDisabled is not null)
                    html.Append($""":disabled="{e.Encode(InputDisabled)}" """);
            }

            html.Append("/>");
        }
        else
        {
            html.Append("""<span class="stepper__val" """);
            if (XText is not null)
                html.Append($"""x-text="{e.Encode(XText)}" """);
            html.Append('>');
            if (DisplayFallback is not null)
                html.Append(e.Encode(DisplayFallback));
            html.Append("</span>");
        }

        // ── Increase button ────────────────────────────────────────────────────
        html.Append($"""<button type="button" class="stepper__btn" aria-label="{e.Encode(IncreaseLabel)}" @click="{e.Encode(IncreaseClick)}" """);
        if (IncreaseDisabled is not null)
            html.Append($""":disabled="{e.Encode(IncreaseDisabled)}" """);
        html.Append('>');
        if (isPill)
            html.Append("+");
        else
            html.Append("""<svg class="icon" aria-hidden="true"><use href="#i-plus" /></svg>""");
        html.Append("</button>");

        html.Append("</div>");

        output.Content.SetHtmlContent(html.ToString());
    }
}
