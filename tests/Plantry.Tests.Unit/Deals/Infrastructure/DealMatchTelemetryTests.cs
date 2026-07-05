using Plantry.Ai.Infrastructure;
using Plantry.Deals.Infrastructure;

namespace Plantry.Tests.Unit.Deals.Infrastructure;

/// <summary>
/// Unit tests pinning the Deals-owned <see cref="DealMatchTelemetry.DealMatchConfidence"/> histogram to the
/// shared <c>Plantry.AI</c> meter. Mirrors the Intake-side guard
/// (<c>AiTelemetryTests.ParseConfidence_Is_Registered_On_The_Shared_AI_Meter</c>): the histogram is created
/// cross-assembly against <see cref="AiTelemetry.Meter"/>, so a wrong meter or metric name would silently drop
/// <c>ai.deal_match.confidence</c> from the single <c>AddMeter("Plantry.AI")</c> subscription in ServiceDefaults
/// without any compile-time signal.
/// </summary>
public sealed class DealMatchTelemetryTests
{
    [Fact]
    public void DealMatchConfidence_Histogram_Name_Is_Correct()
    {
        // The histogram name becomes the metric name in the OTEL backend.
        // Changing it is a breaking change to dashboards / alerts.
        Assert.Equal("ai.deal_match.confidence", DealMatchTelemetry.DealMatchConfidence.Name);
    }

    [Fact]
    public void DealMatchConfidence_Is_Registered_On_The_Shared_AI_Meter()
    {
        // The Deals-owned histogram must be created against the shared "Plantry.AI" meter so the
        // single AddMeter(AiTelemetry.SourceName) subscription in ServiceDefaults captures it.
        Assert.Equal(AiTelemetry.SourceName, DealMatchTelemetry.DealMatchConfidence.Meter.Name);
    }
}
