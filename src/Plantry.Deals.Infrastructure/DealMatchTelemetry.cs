using System.Diagnostics.Metrics;
using Plantry.Ai.Infrastructure;

namespace Plantry.Deals.Infrastructure;

/// <summary>
/// Deals-specific AI metrics. Context-owned instruments created against the shared
/// <see cref="AiTelemetry.Meter"/> (named <see cref="AiTelemetry.SourceName"/>), so the single
/// <c>AddMeter("Plantry.AI")</c> subscription in <c>ServiceDefaults</c> still captures them while the
/// generic AI library stays free of Deals-specific knowledge. Distinct from <c>FlyerTelemetry</c>, which
/// covers the Flipp source's own <c>Plantry.Deals</c> span source.
/// </summary>
public static class DealMatchTelemetry
{
    /// <summary>
    /// Histogram of per-deal match-confidence scores emitted by <see cref="DealMatcher"/>.
    /// Values are in [0, 1]: 1.0 for <c>high</c>, 0.5 for <c>low</c>, 0.0 for <c>none</c>.
    /// Query as <c>ai.deal_match.confidence</c> in your metrics backend.
    /// </summary>
    public static readonly Histogram<double> DealMatchConfidence =
        AiTelemetry.Meter.CreateHistogram<double>(
            "ai.deal_match.confidence",
            unit: "1",
            description: "AI-assigned match-confidence for each flyer deal (high=1.0, low=0.5, none=0.0).");
}
