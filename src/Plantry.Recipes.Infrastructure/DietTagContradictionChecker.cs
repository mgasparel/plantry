using System.ClientModel;
using System.Diagnostics;
using System.Text;
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
/// <see cref="IDietTagContradictionChecker"/> over an OpenAI-compatible <c>ChatClient</c> — the edit-moment
/// diet-tag contradiction checker (plantry-qll2.3). The recipes twin of qll2.2's <c>RecipeTagSuggester</c> and
/// Deals' <c>DealMatcher</c>: ingredient names plus the recipe's Diet-category tag names go in, a small set of
/// ingredient/tag clashes comes back. Uses the same global <see cref="AiOptions"/>/<c>ChatClient</c> setup the
/// other AI seams use — no per-household key, and no model-tier concept (ADR-007 leaves per-task model selection
/// open; the single <see cref="AiOptions.Model"/> is used as-is).
///
/// <para>The AI is an untrusted external function (ADR-007): the surface returns <b>observations</b> and cannot
/// change a tag — acting on the nudge is the user's own tap (Gate 5). Every failure path — an API error, an empty
/// response, or malformed JSON — is a <em>soft</em> failure: it degrades to an empty list (no nudge) and never
/// throws into the caller.</para>
///
/// <para>Observability (Gate 9): each call is wrapped in an <see cref="Activity"/> span
/// (<c>recipe_diet_nudge</c>) carrying <c>ai.model</c> + token-usage attributes only — no ingredient names, tag
/// names, prompt, or API key is ever written to a log or span attribute.</para>
/// </summary>
public sealed class DietTagContradictionChecker : IDietTagContradictionChecker
{
    /// <summary>Upper bound on contradictions returned — the nudge is a one-liner; a short cap keeps it bounded.</summary>
    private const int MaxContradictions = 4;

    private readonly ChatClient _chat;
    private readonly string _modelId;
    private readonly ILogger<DietTagContradictionChecker> _logger;

    public DietTagContradictionChecker(
        IOptions<AiOptions> options,
        ILogger<DietTagContradictionChecker> logger)
    {
        _logger = logger;
        var ai = options.Value;
        _modelId = ai.Model;
        var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(ai.BaseUrl) };
        _chat = new OpenAIClient(new ApiKeyCredential(ai.ApiKey), clientOptions)
            .GetChatClient(ai.Model);
    }

    private const string SystemPrompt = """
        A home cook has tagged ONE recipe with dietary/style tags and just edited its ingredients. You are
        given the recipe's ingredient names and the recipe's DIET tags. Flag ONLY ingredients that plainly
        contradict one of those diet tags — e.g. parmesan or milk against "Dairy-Free"; chicken or fish
        against "Vegetarian"; honey against "Vegan"; wheat/flour against "Gluten-Free".
        Return ONLY valid JSON, no markdown.

        Output format:
        {
          "contradictions": [
            { "ingredient": "the offending ingredient name, copied verbatim from the list",
              "tag": "the diet tag it contradicts, copied verbatim from the diet tags" }
          ]
        }

        Rules:
        - Only flag a clear, obvious contradiction a reasonable cook would agree with at a glance. When in
          doubt, do NOT flag it — a false alarm costs the cook a dismissal and erodes trust.
        - "ingredient" MUST be copied verbatim from the given ingredient list; "tag" MUST be copied verbatim
          from the given diet tags. Never invent either.
        - Each contradiction pairs exactly one ingredient with exactly one diet tag. At most 4, fewer is better.
        - Return an empty "contradictions" array when nothing clearly contradicts (this is the common case).
        - Never flag an ingredient that is compatible or merely unrelated to the tag.
        """;

    public async Task<IReadOnlyList<DietTagContradiction>> CheckAsync(
        IReadOnlyList<string> ingredientNames,
        IReadOnlyList<string> dietTagNames,
        CancellationToken ct = default)
    {
        if (ingredientNames.Count == 0 || dietTagNames.Count == 0)
            return [];

        // Gate 9: span wraps the full AI call. Attributes: model id + token usage only — never ingredient
        // or tag content.
        using var activity = AiTelemetry.ActivitySource.StartActivity("recipe_diet_nudge");
        activity?.SetTag("ai.model", _modelId);

        var sw = Stopwatch.StartNew();
        _logger.LogInformation(
            "AI diet-tag contradiction check starting. Model: {Model}, Ingredients: {IngredientCount}, DietTags: {TagCount}.",
            _modelId, ingredientNames.Count, dietTagNames.Count);

        try
        {
            var userMessage = new UserChatMessage(BuildUserMessage(ingredientNames, dietTagNames));

            var response = await _chat.CompleteChatAsync(
                [new SystemChatMessage(SystemPrompt), userMessage],
                cancellationToken: ct);

            var completion = response.Value;
            RecordTokenUsage(activity, completion.Usage);

            var rawText = completion.Content.Count > 0 ? completion.Content[0].Text : null;

            if (string.IsNullOrWhiteSpace(rawText))
            {
                // Gate 9: empty response is a soft failure but must surface as an error span + log.
                activity?.SetStatus(ActivityStatusCode.Error, "AI returned an empty response.");
                _logger.LogError(
                    "AI diet-tag contradiction check returned an empty response. Model: {Model}, ElapsedMs: {ElapsedMs}.",
                    _modelId, sw.ElapsedMilliseconds);
                return [];
            }

            var contradictions = MapResponse(rawText, ingredientNames, dietTagNames);

            sw.Stop();
            _logger.LogInformation(
                "AI diet-tag contradiction check completed. Model: {Model}, Contradictions: {Count}, ElapsedMs: {ElapsedMs}.",
                _modelId, contradictions.Count, sw.ElapsedMilliseconds);

            return contradictions;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex,
                "AI diet-tag contradiction check failed with exception. Model: {Model}, ElapsedMs: {ElapsedMs}.",
                _modelId, sw.ElapsedMilliseconds);
            return [];
        }
    }

    private static string BuildUserMessage(
        IReadOnlyList<string> ingredientNames, IReadOnlyList<string> dietTagNames)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Recipe ingredients:");
        foreach (var name in ingredientNames)
            sb.Append("- ").AppendLine(name);
        sb.AppendLine();

        sb.AppendLine("Recipe diet tags (flag ingredients that contradict any of these):");
        foreach (var tag in dietTagNames)
            sb.Append("- ").AppendLine(tag);
        return sb.ToString();
    }

    /// <summary>
    /// Maps the model's raw text response into <see cref="DietTagContradiction"/>s. Strips markdown fences, parses
    /// the JSON, and enforces the untrusted-input contract (ADR-007): a contradiction is kept only when both its
    /// <c>ingredient</c> matches one of the supplied ingredient names AND its <c>tag</c> matches one of the supplied
    /// diet tag names (case-insensitive) — the model cannot invent either. The verbatim household spelling is
    /// carried back. Duplicates (by ingredient+tag) collapse, and the list is capped at
    /// <see cref="MaxContradictions"/>. Any malformed/empty content soft-fails to an empty list. Extracted as a pure
    /// static method so it is unit-testable against recorded fixtures with no live call.
    /// </summary>
    internal static IReadOnlyList<DietTagContradiction> MapResponse(
        string? rawContent, IReadOnlyList<string> ingredientNames, IReadOnlyList<string> dietTagNames)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
            return [];

        var ingredientByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in ingredientNames)
            if (!string.IsNullOrWhiteSpace(n)) ingredientByName.TryAdd(n.Trim(), n.Trim());

        var tagByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in dietTagNames)
            if (!string.IsNullOrWhiteSpace(t)) tagByName.TryAdd(t.Trim(), t.Trim());

        try
        {
            using var doc = JsonDocument.Parse(StripFences(rawContent));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("contradictions", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                return [];

            var result = new List<DietTagContradiction>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;

                var ingredient = GetString(el, "ingredient")?.Trim();
                var tag = GetString(el, "tag")?.Trim();
                if (string.IsNullOrWhiteSpace(ingredient) || string.IsNullOrWhiteSpace(tag)) continue;

                // Untrusted-input guard: both sides must resolve to supplied values, else drop.
                if (!ingredientByName.TryGetValue(ingredient, out var canonicalIngredient)) continue;
                if (!tagByName.TryGetValue(tag, out var canonicalTag)) continue;

                if (!seen.Add($"{canonicalIngredient} {canonicalTag}")) continue;

                result.Add(new DietTagContradiction(canonicalIngredient, canonicalTag));
                if (result.Count >= MaxContradictions) break;
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
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

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static void RecordTokenUsage(Activity? activity, ChatTokenUsage? usage)
    {
        if (usage is null || activity is null) return;
        activity.SetTag("ai.usage.input_tokens", usage.InputTokenCount);
        activity.SetTag("ai.usage.output_tokens", usage.OutputTokenCount);
    }
}
