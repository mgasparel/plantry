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

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var content = await output.GetChildContentAsync();

        var fullName = ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(For.Name);
        var id = TagBuilder.CreateSanitizedId(fullName, htmlGenerator.IdAttributeDotReplacement);
        var label = Label ?? For.Metadata.DisplayName ?? For.Metadata.PropertyName ?? For.Name;

        output.TagName = "div";

        if (Stacked)
        {
            var cls = Full ? "form-grid__field form-grid__field--full" : "form-grid__field";
            output.Attributes.SetAttribute("class", cls);
            output.PreContent.SetHtmlContent(
                $"""<label class="form-grid__field__label" for="{id}">{HtmlEncoder.Default.Encode(label)}</label><div class="form-grid__field__control">""");
        }
        else
        {
            output.Attributes.SetAttribute("class", "field-row");
            output.PreContent.SetHtmlContent(
                $"""<label class="field-row__label" for="{id}">{HtmlEncoder.Default.Encode(label)}</label><div class="field-row__control">""");
        }

        output.Content.SetHtmlContent(content);
        output.PostContent.SetHtmlContent("</div>");
    }
}
