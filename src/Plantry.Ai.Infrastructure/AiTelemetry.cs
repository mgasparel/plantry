using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Plantry.Ai.Infrastructure;

/// <summary>
/// Shared, generic telemetry primitives for the Plantry AI pipeline.
/// <para>
/// Every AI adapter — <c>GeminiReceiptParser</c> (Intake), <c>MealPlannerAiService</c>
/// (MealPlanning), and <c>DealMatcher</c> (Deals) — emits its spans through <see cref="ActivitySource"/>
/// and records its metrics on a histogram created against <see cref="Meter"/>, so all AI telemetry
/// appears under a single, consistently-named source that the OTEL SDK subscribes to with one
/// <c>AddSource</c> / <c>AddMeter</c> call in <c>ServiceDefaults.ConfigureOpenTelemetry</c>.
/// </para>
/// <para>
/// This type holds only the generic primitives (source name, activity source, meter).
/// Context-specific instruments live with their owning context and are created against
/// <see cref="Meter"/> so the single <c>AddMeter(SourceName)</c> subscription still sees them —
/// e.g. <c>ai.parse.confidence</c> (Intake) and <c>ai.deal_match.confidence</c> (Deals).
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
    /// Use <see cref="ActivitySource.StartActivity(string)"/> from each AI adapter
    /// (receipt_parse, meal_plan_propose, deal_match).
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(SourceName, "1.0.0");

    /// <summary>
    /// The shared <see cref="Meter"/> (named <see cref="SourceName"/>) that all AI instruments are
    /// created against. Context-owned histograms create their instruments here so a single
    /// <c>AddMeter(SourceName)</c> subscription in ServiceDefaults captures every AI metric.
    /// </summary>
    public static readonly Meter Meter = new(SourceName, "1.0.0");
}
