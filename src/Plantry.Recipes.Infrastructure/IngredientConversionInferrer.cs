using System.ClientModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using Plantry.Ai.Infrastructure;
using Plantry.Recipes.Application;

namespace Plantry.Recipes.Infrastructure;

/// <summary>
/// <see cref="IIngredientConversionInferrer"/> over an OpenAI-compatible <c>ChatClient</c> — the async
/// post-save density/cross-measure factor inference for a recipe unit-gap (plantry-qll2.4, ADR-022). The
/// recipes twin of qll2.2's <c>RecipeTagSuggester</c> and qll2.3's <c>DietTagContradictionChecker</c>: a
/// product name plus the ordered unit pair (recipe-line unit → product stock unit) go in, a single factor
/// comes back. Uses the same global <see cref="AiOptions"/>/<c>ChatClient</c> setup the other AI seams use
/// — no per-household key, and no model-tier concept (ADR-007 leaves per-task model selection open; the
/// single <see cref="AiOptions.Model"/> is used as-is).
///
/// <para>The AI is an untrusted external function (ADR-007 / Gate 5): the factor is a <b>provisional
/// reference value</b>, not a state change (ADR-022). Every failure path — an API error, an empty
/// response, malformed JSON, or a non-positive / non-finite / absurd factor — is a <em>soft</em> failure:
/// it degrades to <c>null</c> (no seed) and never throws into the caller.</para>
///
/// <para>Observability (Gate 9): each call is wrapped in an <see cref="Activity"/> span
/// (<c>recipe_conversion_seed</c>) carrying <c>ai.model</c> + token-usage attributes only — no product
/// name, unit codes, prompt, or API key is ever written to a log or span attribute.</para>
/// </summary>
public sealed class IngredientConversionInferrer : IIngredientConversionInferrer
{
    /// <summary>
    /// Sanity bound on the returned factor. A per-product unit conversion factor spans a wide but finite
    /// range (grams-per-cup ≈ 10²; each-per-kilogram of a light item ≈ 10¹; tiny factors for the inverse).
    /// Anything outside (0, 1e6] is treated as a hallucination and dropped — precision is not a goal, but
    /// a nonsense factor must not silently corrupt pantry math.
    /// </summary>
    private const decimal MaxFactor = 1_000_000m;

    private readonly ChatClient _chat;
    private readonly string _modelId;
    private readonly ILogger<IngredientConversionInferrer> _logger;

    public IngredientConversionInferrer(
        IOptions<AiOptions> options,
        ILogger<IngredientConversionInferrer> logger)
    {
        _logger = logger;
        var ai = options.Value;
        _modelId = ai.Model;
        var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(ai.BaseUrl) };
        _chat = new OpenAIClient(new ApiKeyCredential(ai.ApiKey), clientOptions)
            .GetChatClient(ai.Model);
    }

    /// <summary>A configured (keyed) inferrer is always available; the keyless host uses the disabled no-op instead.</summary>
    public bool IsAvailable => true;

    private const string SystemPrompt = """
        You are a kitchen unit-conversion assistant. Given a grocery product and an ordered pair of units,
        estimate the single multiplicative factor that converts ONE "from" unit of that product into "to"
        units — i.e. how many "to" units equal one "from" unit for this specific product. This is a
        per-product density / count relationship (e.g. 1 cup of cashews ≈ 120 g; 1 lb of bananas ≈ 4 each).

        Return ONLY valid JSON, no markdown:
        { "factor": <number> }

        Rules:
        - "factor" is a positive number: (quantity in the TO unit) that equals one FROM unit of the product.
        - Give your best single estimate for a typical form of the product. Approximate is fine; precision
          is not required.
        - If you genuinely cannot estimate it (the product or units make no sense together), return
          { "factor": null }.
        - Never return zero, a negative number, or text — just the JSON object.
        """;

    public async Task<decimal?> InferFactorAsync(
        string productName, string fromUnitCode, string toUnitCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(productName)
            || string.IsNullOrWhiteSpace(fromUnitCode)
            || string.IsNullOrWhiteSpace(toUnitCode))
            return null;

        // Gate 9: span wraps the full AI call. Attributes: model id + token usage only — never product
        // name or unit content.
        using var activity = AiTelemetry.ActivitySource.StartActivity("recipe_conversion_seed");
        activity?.SetTag("ai.model", _modelId);

        var sw = Stopwatch.StartNew();
        _logger.LogInformation(
            "AI conversion inference starting. Model: {Model}.", _modelId);

        try
        {
            var userMessage = new UserChatMessage(
                $"Product: {productName}\nFrom unit: {fromUnitCode}\nTo unit: {toUnitCode}\n"
                + $"How many {toUnitCode} equal one {fromUnitCode} of this product?");

            var response = await _chat.CompleteChatAsync(
                [new SystemChatMessage(SystemPrompt), userMessage],
                cancellationToken: ct);

            var completion = response.Value;
            RecordTokenUsage(activity, completion.Usage);

            var rawText = completion.Content.Count > 0 ? completion.Content[0].Text : null;

            if (string.IsNullOrWhiteSpace(rawText))
            {
                activity?.SetStatus(ActivityStatusCode.Error, "AI returned an empty response.");
                _logger.LogError(
                    "AI conversion inference returned an empty response. Model: {Model}, ElapsedMs: {ElapsedMs}.",
                    _modelId, sw.ElapsedMilliseconds);
                return null;
            }

            var factor = ParseFactor(rawText);

            sw.Stop();
            _logger.LogInformation(
                "AI conversion inference completed. Model: {Model}, HasFactor: {HasFactor}, ElapsedMs: {ElapsedMs}.",
                _modelId, factor.HasValue, sw.ElapsedMilliseconds);

            return factor;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex,
                "AI conversion inference failed with exception. Model: {Model}, ElapsedMs: {ElapsedMs}.",
                _modelId, sw.ElapsedMilliseconds);
            return null;
        }
    }

    /// <summary>
    /// Parses the model's raw text into a validated factor. Strips markdown fences, reads the
    /// <c>factor</c> number, and enforces the untrusted-input contract (ADR-007): the value must be a
    /// finite, strictly positive number within <see cref="MaxFactor"/>; anything else — a null factor,
    /// zero/negative, NaN/∞, out-of-range, or malformed JSON — soft-fails to <c>null</c>. Extracted as a
    /// pure static method so it is unit-testable against recorded fixtures with no live call.
    /// </summary>
    internal static decimal? ParseFactor(string? rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(StripFences(rawContent));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("factor", out var el))
                return null;

            decimal value;
            switch (el.ValueKind)
            {
                case JsonValueKind.Number:
                    if (!el.TryGetDecimal(out value))
                    {
                        // A number too large/small for decimal (e.g. 1e40) is a hallucination — drop it.
                        return null;
                    }
                    break;
                case JsonValueKind.String:
                    // Some models return the number as a quoted string; accept a clean parse only.
                    if (!decimal.TryParse(
                            el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                        return null;
                    break;
                default:
                    // null / bool / object / array → no usable factor.
                    return null;
            }

            if (value <= 0m || value > MaxFactor)
                return null;

            return value;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Unwraps a ```json … ``` (or bare ``` … ```) markdown fence; an unfenced response falls through to
    // the trimmed input. Singleline makes '.' span newlines so multi-line JSON is captured whole.
    private static readonly Regex FencePattern = new(
        @"^\s*```(?:json)?\s*(.*?)\s*```\s*$",
        RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string StripFences(string raw)
    {
        var match = FencePattern.Match(raw);
        return match.Success ? match.Groups[1].Value : raw.Trim();
    }

    private static void RecordTokenUsage(Activity? activity, ChatTokenUsage? usage)
    {
        if (usage is null || activity is null) return;
        activity.SetTag("ai.usage.input_tokens", usage.InputTokenCount);
        activity.SetTag("ai.usage.output_tokens", usage.OutputTokenCount);
    }
}
