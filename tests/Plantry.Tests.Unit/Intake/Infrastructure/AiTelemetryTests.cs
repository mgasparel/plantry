using System.Diagnostics;
using Plantry.Intake.Infrastructure;

namespace Plantry.Tests.Unit.Intake.Infrastructure;

/// <summary>
/// Unit tests for <see cref="AiTelemetry"/> primitives and the
/// <see cref="GeminiReceiptParser.ConfidenceScore"/> mapping.
///
/// Span emission and log calls require a live <c>ChatClient</c> and cannot be exercised here
/// (those paths are validated via E2E or integration tests with the fake parser). These tests
/// cover the pure-function / compile-time surface: correct source name (required for
/// <c>AddSource("Plantry.AI")</c> in ServiceDefaults to subscribe), correct histogram name
/// (required for metrics backends to query), and correct confidence-to-score mapping
/// (drives the histogram values recorded for each receipt line).
/// </summary>
public sealed class AiTelemetryTests
{
    // ── Source / meter name contract ────────────────────────────────────────────────────────────

    [Fact]
    public void SourceName_Is_PlantryAI()
    {
        // ServiceDefaults registers AddSource("Plantry.AI") and AddMeter("Plantry.AI").
        // Changing this constant without updating ServiceDefaults would silently stop
        // all AI spans and metrics from being exported.
        Assert.Equal("Plantry.AI", AiTelemetry.SourceName);
    }

    [Fact]
    public void ActivitySource_Name_Matches_SourceName()
    {
        Assert.Equal(AiTelemetry.SourceName, AiTelemetry.ActivitySource.Name);
    }

    [Fact]
    public void ParseConfidence_Histogram_Name_Is_Correct()
    {
        // The histogram name becomes the metric name in the OTEL backend.
        // Changing it is a breaking change to dashboards / alerts.
        Assert.Equal("ai.parse.confidence", AiTelemetry.ParseConfidence.Name);
    }

    // ── ConfidenceScore mapping ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("high", 1.0)]
    [InlineData("low",  0.5)]
    [InlineData("none", 0.0)]
    [InlineData(null,   0.0)]
    [InlineData("",     0.0)]
    [InlineData("HIGH", 0.0)]  // case-sensitive — AI outputs lowercase per the prompt
    public void ConfidenceScore_Maps_Label_To_Expected_Value(string? label, double expected)
    {
        Assert.Equal(expected, GeminiReceiptParser.ConfidenceScore(label));
    }

    // ── ActivitySource can start an activity when a listener is registered ───────────────────────

    [Fact]
    public void ActivitySource_Starts_Activity_When_Listener_Present()
    {
        // ActivitySource.StartActivity returns null when no listener is subscribed.
        // This test subscribes a listener to verify that the source name "Plantry.AI"
        // actually reaches the SDK — confirming the contract used by AddSource("Plantry.AI").
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Plantry.AI",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = _ => { },
            ActivityStopped = _ => { },
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = AiTelemetry.ActivitySource.StartActivity("test_span");

        Assert.NotNull(activity);
        Assert.Equal("test_span", activity.OperationName);
    }
}
