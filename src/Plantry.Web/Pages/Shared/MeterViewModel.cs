namespace Plantry.Web.Pages.Shared;

/// <summary>
/// Tone of the meter fill bar — maps to the existing fulf-* color tokens plus accent and neutral.
/// </summary>
public enum MeterTone
{
    /// <summary>fulf-hi (green / success, ≥80%)</summary>
    Hi,
    /// <summary>fulf-mid (amber / warning, ≥50%)</summary>
    Mid,
    /// <summary>fulf-lo (red / danger, &lt;50%)</summary>
    Lo,
    /// <summary>--color-accent (the theme accent; used by the intake review bar)</summary>
    Accent,
    /// <summary>Neutral gray — no semantic tinting</summary>
    Neutral,
}

/// <summary>
/// Display variant of the meter — governs label layout only; the bar itself is the same.
/// </summary>
public enum MeterLayout
{
    /// <summary>
    /// Bar + right-aligned label column: bold percentage above, faint sub-label below.
    /// Used by the recipes grid fulfillment meter.
    /// </summary>
    PctStack,

    /// <summary>
    /// Bar + trailing meta text (inline, single line). Used by the intake review progress bar.
    /// The meta content is rendered via the <see cref="MeterViewModel.MetaHtml"/> slot.
    /// </summary>
    MetaLine,

    /// <summary>
    /// Bar only, no label. Used when the caller renders its own surrounding context (e.g.
    /// the recipe detail fulfillment card). Aria role/values must be supplied by the caller
    /// via <see cref="MeterViewModel.AriaValueNow"/>.
    /// </summary>
    BarOnly,
}

/// <summary>
/// View model for the reusable <c>_Meter</c> partial — a horizontal fill bar tinted by level
/// with optional labels. Replaces the three ad-hoc bar implementations:
/// <list type="bullet">
/// <item><description><c>.rev-progress</c> — intake review header bar</description></item>
/// <item><description><c>.fulf-meter</c> — recipes grid fulfillment bar</description></item>
/// <item><description><c>.rd-fulf-bar-wrap/.rd-fulf-bar-fill</c> — recipe detail fulfillment bar</description></item>
/// </list>
/// </summary>
public sealed record MeterViewModel
{
    /// <summary>Fill percentage, 0–100. Server-computed; never recalculated client-side (Gate 6).</summary>
    public required int Percent { get; init; }

    /// <summary>Tone of the fill bar.</summary>
    public required MeterTone Tone { get; init; }

    /// <summary>Layout variant controlling which label slots are rendered.</summary>
    public MeterLayout Layout { get; init; } = MeterLayout.BarOnly;

    /// <summary>
    /// Primary label — percentage text, e.g. "90%". Only rendered when
    /// <see cref="Layout"/> is <see cref="MeterLayout.PctStack"/>.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// Secondary label — faint sub-text, e.g. "9/10 in stock". Only rendered when
    /// <see cref="Layout"/> is <see cref="MeterLayout.PctStack"/>.
    /// </summary>
    public string? SubLabel { get; init; }

    /// <summary>
    /// Trailing meta HTML slot — inline text after the bar. Only rendered when
    /// <see cref="Layout"/> is <see cref="MeterLayout.MetaLine"/>. The caller
    /// supplies pre-escaped HTML (from Razor <c>@()</c> expressions).
    /// </summary>
    public string? MetaHtml { get; init; }

    /// <summary>
    /// When set, renders <c>role="progressbar"</c> with <c>aria-valuenow</c>,
    /// <c>aria-valuemin="0"</c>, <c>aria-valuemax="100"</c> on the bar track element.
    /// Leave null to omit these attributes (e.g. when the bar is decorative and the
    /// surrounding context already provides the accessible label).
    /// </summary>
    public int? AriaValueNow { get; init; }

    /// <summary>
    /// Optional extra CSS class(es) on the outer <c>.meter</c> wrapper — used by callers
    /// that need layout overrides (e.g. margin, width constraint).
    /// </summary>
    public string? WrapperClass { get; init; }

    // ── Static helpers ───────────────────────────────────────────────────────

    /// <summary>Map a fulfillment percentage to the correct tone (≥80 hi, ≥50 mid, else lo).</summary>
    public static MeterTone FulfTone(int pct) => pct >= 80 ? MeterTone.Hi : pct >= 50 ? MeterTone.Mid : MeterTone.Lo;

    // ── Derived helpers consumed by the partial ──────────────────────────────

    /// <summary>CSS class name for the fill element (background colour token).</summary>
    public string FillClass => Tone switch
    {
        MeterTone.Hi      => "fulf-hi-bg",
        MeterTone.Mid     => "fulf-mid-bg",
        MeterTone.Lo      => "fulf-lo-bg",
        MeterTone.Accent  => "meter__fill--accent",
        _                 => "meter__fill--neutral",
    };

    /// <summary>CSS class name for the percentage text colour token.</summary>
    public string TextClass => Tone switch
    {
        MeterTone.Hi  => "fulf-hi",
        MeterTone.Mid => "fulf-mid",
        MeterTone.Lo  => "fulf-lo",
        _             => "",
    };
}
