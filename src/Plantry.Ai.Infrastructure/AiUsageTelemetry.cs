using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenAI.Chat;

namespace Plantry.Ai.Infrastructure;

/// <summary>
/// Shared token-usage recording for every AI adapter (plantry-df6p). Was previously copy-pasted as a
/// private <c>RecordTokenUsage(Activity?, ChatTokenUsage?)</c> in all six adapters
/// (<c>GeminiReceiptParser</c>, <c>DealMatcher</c>, <c>MealPlannerAiService</c>,
/// <c>IngredientConversionInferrer</c>, <c>RecipeTagSuggester</c>, <c>DietTagContradictionChecker</c>) —
/// lifted here so the span-tag shape and the metric can never drift between adapters, and so any future
/// adapter gets both for free by calling this one method.
/// </summary>
public static class AiUsageTelemetry
{
    /// <summary>
    /// Counter of AI tokens consumed, dimensioned by <c>ai.function</c> (see <see cref="AiFunction"/>),
    /// <c>ai.model</c>, and <c>ai.token_kind</c> (<c>input</c>/<c>output</c>) — answers "which feature
    /// drove the AI bill" from metrics (sampled, retention-limited spans cannot). Created against the
    /// shared <see cref="AiTelemetry.Meter"/> so the single <c>AddMeter(AiTelemetry.SourceName)</c>
    /// subscription in <c>ServiceDefaults</c> captures it. Query as <c>ai.usage.tokens</c> in your
    /// metrics backend.
    /// </summary>
    public static readonly Counter<long> TokensUsed =
        AiTelemetry.Meter.CreateCounter<long>(
            "ai.usage.tokens",
            unit: "{token}",
            description: "AI tokens consumed, by function, model, and input/output kind.");

    /// <summary>
    /// Records token usage on the call's <see cref="Activity"/> span (<c>ai.usage.input_tokens</c> /
    /// <c>ai.usage.output_tokens</c> — unchanged shape, nothing downstream breaks) and on
    /// <see cref="TokensUsed"/> (dimensioned by <paramref name="function"/>, <paramref name="model"/>,
    /// and token kind). No-op when <paramref name="usage"/> is null (a failed/empty completion never
    /// reports usage). Guard (Gate 9 §PII): only the <see cref="AiFunction"/> constant, the model id, and
    /// counts are recorded — never prompt/response content, ingredient names, or tag names.
    /// </summary>
    public static void RecordTokenUsage(Activity? activity, ChatTokenUsage? usage, string function, string model)
    {
        if (usage is null) return;

        activity?.SetTag("ai.usage.input_tokens", usage.InputTokenCount);
        activity?.SetTag("ai.usage.output_tokens", usage.OutputTokenCount);

        TokensUsed.Add(usage.InputTokenCount,
            new KeyValuePair<string, object?>("ai.function", function),
            new KeyValuePair<string, object?>("ai.model", model),
            new KeyValuePair<string, object?>("ai.token_kind", "input"));
        TokensUsed.Add(usage.OutputTokenCount,
            new KeyValuePair<string, object?>("ai.function", function),
            new KeyValuePair<string, object?>("ai.model", model),
            new KeyValuePair<string, object?>("ai.token_kind", "output"));
    }
}
