using System.Diagnostics;
using Plantry.Ai.Infrastructure;
using Plantry.Tests.Unit.TestSupport;

namespace Plantry.Tests.Unit.Ai.Infrastructure;

/// <summary>
/// Unit tests for the shared <see cref="AiFunction"/> taxonomy and <see cref="AiUsageTelemetry"/>
/// helper (plantry-df6p) — the single <c>RecordTokenUsage</c> lifted out of the AI adapters
/// (GeminiReceiptParser, DealMatcher, IngredientConversionInferrer,
/// RecipeTagSuggester, DietTagContradictionChecker).
/// </summary>
public sealed class AiUsageTelemetryTests
{
    // ── AiFunction taxonomy: no renames — these ARE the existing span names ─────────────────────

    [Fact]
    public void AiFunction_Constants_Match_The_Existing_Span_Names()
    {
        Assert.Equal("receipt_parse", AiFunction.ReceiptParse);
        Assert.Equal("deal_match", AiFunction.DealMatch);
        Assert.Equal("recipe_conversion_seed", AiFunction.RecipeConversionSeed);
        Assert.Equal("recipe_tag_suggest", AiFunction.RecipeTagSuggest);
        Assert.Equal("recipe_diet_nudge", AiFunction.RecipeDietNudge);
    }

    // ── TokensUsed counter contract ──────────────────────────────────────────────────────────────

    [Fact]
    public void TokensUsed_Counter_Name_Is_Correct()
    {
        // The counter name becomes the metric name in the OTEL backend (Grafana/Prometheus query).
        Assert.Equal("ai.usage.tokens", AiUsageTelemetry.TokensUsed.Name);
    }

    [Fact]
    public void TokensUsed_Is_Registered_On_The_Shared_AI_Meter()
    {
        // Must be created against the shared "Plantry.AI" meter so the single
        // AddMeter(AiTelemetry.SourceName) subscription in ServiceDefaults captures it.
        Assert.Equal(AiTelemetry.SourceName, AiUsageTelemetry.TokensUsed.Meter.Name);
    }

    // ── RecordTokenUsage: span tags (shape must stay identical — nothing downstream breaks) ───────

    [Fact]
    public void RecordTokenUsage_Sets_Span_Tags_When_Usage_Present()
    {
        AiSpanCapture.Capture(null, out var listener);
        using var _1 = listener;

        using var activity = AiTelemetry.ActivitySource.StartActivity("test_span");
        var usage = ScriptedChatClient.Usage(inputTokens: 123, outputTokens: 45);

        AiUsageTelemetry.RecordTokenUsage(activity, usage, AiFunction.ReceiptParse, "test-model");

        Assert.Equal(123, activity!.GetTagItem("ai.usage.input_tokens"));
        Assert.Equal(45, activity.GetTagItem("ai.usage.output_tokens"));
    }

    [Fact]
    public void RecordTokenUsage_Does_Not_Throw_When_Activity_Is_Null()
    {
        var usage = ScriptedChatClient.Usage(inputTokens: 10, outputTokens: 5);

        var ex = Record.Exception(() =>
            AiUsageTelemetry.RecordTokenUsage(null, usage, AiFunction.DealMatch, "test-model"));

        Assert.Null(ex);
    }

    [Fact]
    public void RecordTokenUsage_Is_A_NoOp_When_Usage_Is_Null()
    {
        AiSpanCapture.Capture(null, out var listener);
        using var _2 = listener;

        using var activity = AiTelemetry.ActivitySource.StartActivity("test_span");
        AiUsageTelemetry.RecordTokenUsage(activity, null, AiFunction.DealMatch, "test-model");


        Assert.Null(activity!.GetTagItem("ai.usage.input_tokens"));
        Assert.Null(activity.GetTagItem("ai.usage.output_tokens"));
    }

    // ── RecordTokenUsage: ai.usage.tokens metric, dimensioned by function/model/token_kind ─────────

    [Fact]
    public void RecordTokenUsage_Emits_Tokens_Metric_Dimensioned_By_Function_Model_And_Kind()
    {
        const string model = "ai-usage-telemetry-dimension-sentinel";
        var measurements = TokenUsageMeasurementCapture.Capture(model, out var meterListener);
        using (meterListener)
        {
            var usage = ScriptedChatClient.Usage(inputTokens: 200, outputTokens: 50);
            AiUsageTelemetry.RecordTokenUsage(null, usage, AiFunction.RecipeTagSuggest, model);
        }

        Assert.Contains(measurements, m => m is (200, AiFunction.RecipeTagSuggest, "input"));
        Assert.Contains(measurements, m => m is (50, AiFunction.RecipeTagSuggest, "output"));
    }

    [Fact]
    public void RecordTokenUsage_Emits_No_Metric_When_Usage_Is_Null()
    {
        const string model = "no-usage-sentinel";
        var measurements = TokenUsageMeasurementCapture.Capture(model, out var meterListener);
        using (meterListener)
        {
            AiUsageTelemetry.RecordTokenUsage(null, null, AiFunction.RecipeDietNudge, model);
        }

        Assert.Empty(measurements);
    }
}
