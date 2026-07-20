using System.Diagnostics.Metrics;
using Plantry.Ai.Infrastructure;

namespace Plantry.Intake.Infrastructure;

/// <summary>
/// Intake-specific AI metrics. These are context-owned instruments created against the shared
/// <see cref="AiTelemetry.Meter"/> (named <see cref="AiTelemetry.SourceName"/>), so the single
/// <c>AddMeter("Plantry.AI")</c> subscription in <c>ServiceDefaults</c> still captures them while the
/// generic AI library stays free of Intake-specific knowledge.
/// </summary>
public static class IntakeAiTelemetry
{
    /// <summary>
    /// Histogram of per-line parse confidence scores emitted by <see cref="GeminiReceiptParser"/>.
    /// Values are in [0, 1]: 1.0 for <c>"high"</c>, 0.5 for <c>"low"</c>, 0.0 for <c>"none"</c>.
    /// Query as <c>ai.parse.confidence</c> in your metrics backend.
    /// </summary>
    public static readonly Histogram<double> ParseConfidence =
        AiTelemetry.Meter.CreateHistogram<double>(
            "ai.parse.confidence",
            unit: "1",
            description: "AI-assigned match-confidence for each receipt line (high=1.0, low=0.5, none=0.0).");

    /// <summary>
    /// Count of receipt parses whose AI-parsed <c>purchase_date</c> fell outside the plausibility window
    /// (later than upload + 1 day, or older than one year) and was dropped by
    /// <see cref="GeminiReceiptParser"/> (plantry-ag05). Keeps gross date misreads — e.g. year-digit
    /// swaps — visible in aggregate without writing the receipt-derived value to any log or span attribute.
    /// Query as <c>ai.parse.implausible_purchase_date</c> in your metrics backend.
    /// </summary>
    public static readonly Counter<long> ImplausiblePurchaseDate =
        AiTelemetry.Meter.CreateCounter<long>(
            "ai.parse.implausible_purchase_date",
            unit: "1",
            description: "Receipt parses whose purchase_date fell outside the plausibility window and was dropped.");
}
