using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Plantry.Web.TagHelpers;

/// <summary>
/// Lays out a label and its control as one row in a fixed two-column grid, replacing
/// `.field--inline` (a row flexbox where each label is sized to its own text, leaving inputs
/// raggedly aligned across rows). The control markup — input/select/validation span — passes
/// through untouched as child content, so `asp-for`, `asp-items`, and `asp-validation-for`
/// keep working exactly as they do today; this tag helper only supplies the label and grid.
/// </summary>
[HtmlTargetElement("field", Attributes = ForAttributeName)]
public sealed class FieldTagHelper(IHtmlGenerator htmlGenerator) : TagHelper
{
    private const string ForAttributeName = "asp-for";
    private static int _hintCounter;

    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = default!;

    [HtmlAttributeName(ForAttributeName)]
    public ModelExpression For { get; set; } = default!;

    /// <summary>Overrides the label text; otherwise derived from [Display(Name)] / the property name.</summary>
    public string? Label { get; set; }

    /// <summary>
    /// When true, renders the stacked form-grid layout (label above control) instead of the
    /// default horizontal field-row layout. Intended for use inside a <c>.form-grid</c> container.
    /// </summary>
    public bool Stacked { get; set; }

    /// <summary>When true (requires <see cref="Stacked"/>), the field spans both columns of the parent form-grid.</summary>
    public bool Full { get; set; }

    /// <summary>
    /// Optional contextual help text. When provided, renders a circled-i info trigger inline
    /// inside the label (using the popover primitive). The trigger is keyboard and touch
    /// reachable; <c>aria-describedby</c> wires the content panel for screen readers.
    /// </summary>
    public string? Hint { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var content = await output.GetChildContentAsync();

        var fullName = ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(For.Name);
        var id = TagBuilder.CreateSanitizedId(fullName, htmlGenerator.IdAttributeDotReplacement);
        var label = Label ?? For.Metadata.DisplayName ?? For.Metadata.PropertyName ?? For.Name;

        output.TagName = "div";

        var labelHtml = BuildLabelHtml(label, id);

        if (Stacked)
        {
            var cls = Full ? "form-grid__field form-grid__field--full" : "form-grid__field";
            output.Attributes.SetAttribute("class", cls);
            output.PreContent.SetHtmlContent(
                $"""{labelHtml}<div class="form-grid__field__control">""");
        }
        else
        {
            output.Attributes.SetAttribute("class", "field-row");
            output.PreContent.SetHtmlContent(
                $"""{labelHtml}<div class="field-row__control">""");
        }

        output.Content.SetHtmlContent(content);
        output.PostContent.SetHtmlContent("</div>");
    }

    /// <summary>
    /// Builds the label element, optionally wrapping label text and an inline popover hint
    /// trigger. When <see cref="Hint"/> is null, produces the same simple label as before.
    /// When set, the label content becomes a <c>.field-hint</c> flex row: label text +
    /// a <c>.popover</c> info trigger with <c>aria-describedby</c> pointing to the panel.
    /// </summary>
    private string BuildLabelHtml(string labelText, string inputId)
    {
        var e = HtmlEncoder.Default;
        var encodedLabel = e.Encode(labelText);

        if (Hint is null)
        {
            return Stacked
                ? $"""<label class="form-grid__field__label" for="{inputId}">{encodedLabel}</label>"""
                : $"""<label class="field-row__label" for="{inputId}">{encodedLabel}</label>""";
        }

        // Unique id for the popover panel — thread-safe across concurrent Razor compilations.
        var popoverId = $"field-hint-{Interlocked.Increment(ref _hintCounter)}";
        var encodedHint = e.Encode(Hint);

        // SVG info icon: circled-i via the sprite symbol.
        const string InfoIcon = """<svg class="icon" aria-hidden="true" width="16" height="16"><use href="#i-info" /></svg>""";

        // Inline popover markup — matches the structure PopoverTagHelper produces so the same
        // CSS rules apply without duplication. Position defaults to "above" (no modifier class).
        var sb = new StringBuilder();
        sb.Append("""<span class="popover">""");
        sb.Append($"""<button type="button" class="popover__trigger" aria-label="More information" aria-describedby="{popoverId}">{InfoIcon}</button>""");
        sb.Append($"""<span class="popover__content" id="{popoverId}" role="tooltip">{encodedHint}</span>""");
        sb.Append("</span>");
        var popoverHtml = sb.ToString();

        var labelClass = Stacked ? "form-grid__field__label field-hint" : "field-row__label field-hint";
        return $"""<label class="{labelClass}" for="{inputId}">{encodedLabel}{popoverHtml}</label>""";
    }
}
