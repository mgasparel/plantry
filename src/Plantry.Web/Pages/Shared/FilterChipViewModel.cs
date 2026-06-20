namespace Plantry.Web.Pages.Shared;

/// <summary>
/// View model for the <c>_FilterChip</c> partial — renders the inner content
/// (leading dot or icon, label, optional trailing count) of a filter chip button.
/// The caller is responsible for the <c>&lt;button&gt;</c> element and its attributes
/// (<c>class</c>, htmx, Alpine, <c>type</c>, etc.).
/// </summary>
/// <param name="Label">Display text for the chip.</param>
/// <param name="DotColor">Optional CSS colour for a leading dot (e.g. <c>var(--color-warning)</c>).
/// Takes precedence over <paramref name="IconId"/> when both are provided.</param>
/// <param name="IconId">Optional SVG sprite id for a leading icon (e.g. <c>i-timer</c>).
/// Ignored when <paramref name="DotColor"/> is set.</param>
/// <param name="Count">Optional trailing count appended after the label.</param>
public sealed record FilterChipViewModel(
    string Label,
    string? DotColor = null,
    string? IconId = null,
    int? Count = null);
