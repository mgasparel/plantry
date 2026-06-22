using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Plantry.Web.TagHelpers;

/// <summary>
/// CSS-only anchored overlay: a trigger badge that reveals a floating tooltip/popover on
/// hover and keyboard focus. Aria wiring (aria-describedby ↔ id) is generated automatically
/// so consumers never wire ids by hand.
///
/// <para>
/// Child content is the popover body and is passed through untouched (trusted developer copy).
/// An optional <see cref="Title"/> renders a bold first line inside the content panel.
/// </para>
///
/// Usage:
/// <code>
/// &lt;!-- Default: above, centered --&gt;
/// &lt;popover&gt;Net quantity is what's left after consumption.&lt;/popover&gt;
///
/// &lt;!-- Below, with title --&gt;
/// &lt;popover position="below" title="Confidence score"&gt;How sure the AI match is.&lt;/popover&gt;
///
/// &lt;!-- Right-aligned, custom label --&gt;
/// &lt;popover position="end" label="i"&gt;This item is staged for review.&lt;/popover&gt;
/// </code>
/// </summary>
[HtmlTargetElement("popover")]
public sealed class PopoverTagHelper : TagHelper
{
    private static int _counter;

    /// <summary>
    /// Placement of the content panel relative to the trigger.
    /// Accepted values: <c>"above"</c> (default), <c>"below"</c>, <c>"start"</c>, <c>"end"</c>.
    /// Maps to modifier class <c>popover--below</c>, <c>popover--start</c>, <c>popover--end</c>;
    /// no modifier class is added for <c>"above"</c>.
    /// </summary>
    public string Position { get; set; } = "above";

    /// <summary>
    /// Glyph shown inside the trigger badge and used as its <c>aria-label</c>.
    /// Defaults to <c>"?"</c>.
    /// </summary>
    public string Label { get; set; } = "?";

    /// <summary>
    /// Optional bold first line inside the content panel.
    /// Renders as <c>&lt;span class="popover__title"&gt;…&lt;/span&gt;</c> before the child content.
    /// </summary>
    public string? Title { get; set; }

    // TagHelper.Order controls execution order relative to other helpers on the same element.
    // Default (0) is fine here.

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var childContent = await output.GetChildContentAsync();
        var e = HtmlEncoder.Default;

        // Generate a unique id. Using Interlocked so concurrent Razor compilations stay safe.
        var id = $"popover-{Interlocked.Increment(ref _counter)}";

        // Resolve modifier class from position.
        var modifier = Position switch
        {
            "below" => " popover--below",
            "start" => " popover--start",
            "end"   => " popover--end",
            _       => string.Empty, // "above" = default, no modifier
        };

        output.TagName = null; // suppress the <popover> element itself

        var html = new StringBuilder();

        // Wrapper
        html.Append($"""<span class="popover{e.Encode(modifier)}">""");

        // Trigger button
        html.Append($"""<button type="button" class="popover__trigger" aria-label="{e.Encode(Label)}" aria-describedby="{e.Encode(id)}">{e.Encode(Label)}</button>""");

        // Content panel
        html.Append($"""<span class="popover__content" id="{e.Encode(id)}" role="tooltip">""");
        if (Title is not null)
            html.Append($"""<span class="popover__title">{e.Encode(Title)}</span>""");
        html.Append(childContent.GetContent());
        html.Append("</span>");

        // Close wrapper
        html.Append("</span>");

        output.Content.SetHtmlContent(html.ToString());
    }
}
