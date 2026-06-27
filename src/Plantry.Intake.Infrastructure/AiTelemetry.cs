using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Plantry.Intake.Infrastructure;

/// <summary>
/// Central telemetry primitives for the Plantry AI pipeline.
/// <para>
/// Both <see cref="GeminiReceiptParser"/> (Intake) and
/// <c>MealPlannerAiService</c> (MealPlanning.Infrastructure) share these primitives so all AI
/// spans and metrics appear under a single, consistently-named source that the OTEL SDK can
/// subscribe to with one <c>AddSource</c> / <c>AddMeter</c> call.
/// </para>
/// <para>
/// <strong>Guard:</strong> no PII or secret material is captured here — only model ids,
/// token counts, durations, and aggregate confidence scores (Gate 9 §PII rule).
/// </para>
/// </summary>
public static class AiTelemetry
{
    /// <summary>
    /// The name used for both the <see cref="ActivitySource"/> and the <see cref="Meter"/>.
    /// Register this in <c>ServiceDefaults.ConfigureOpenTelemetry</c> via
    /// <c>AddSource(AiTelemetry.SourceName)</c> and <c>AddMeter(AiTelemetry.SourceName)</c>.
    /// </summary>
    public const string SourceName = "Plantry.AI";

    /// <summary>
    /// <see cref="ActivitySource"/> that emits one <see cref="Activity"/> span per AI call.
    /// Use <see cref="ActivitySource.StartActivity(string)"/> from each AI adapter.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(SourceName, "1.0.0");

    private static readonly Meter Meter = new(SourceName, "1.0.0");

    /// <summary>
    /// Histogram of per-line parse confidence scores emitted by <see cref="GeminiReceiptParser"/>.
    /// Values are in [0, 1]: 1.0 for <c>"high"</c>, 0.5 for <c>"low"</c>, 0.0 for <c>"none"</c>.
    /// Query as <c>ai.parse.confidence</c> in your metrics backend.
    /// </summary>
    public static readonly Histogram<double> ParseConfidence =
        Meter.CreateHistogram<double>(
            "ai.parse.confidence",
            unit: "1",
            description: "AI-assigned match-confidence for each receipt line (high=1.0, low=0.5, none=0.0).");
}
