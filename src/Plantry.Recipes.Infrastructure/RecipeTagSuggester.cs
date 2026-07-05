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
using Plantry.Recipes.Domain;

namespace Plantry.Recipes.Infrastructure;

/// <summary>
/// <see cref="IRecipeTagSuggester"/> over an OpenAI-compatible <c>ChatClient</c> — the edit-moment tag
/// suggester (plantry-qll2.2). The recipes twin of Deals' <c>DealMatcher</c> and Intake's receipt parser:
/// ingredient names plus the household's existing tag vocabulary go in, a small set of proposed tags comes
/// back. Uses the same global <see cref="AiOptions"/>/<c>ChatClient</c> setup Intake/MealPlanning/Deals use
/// today — no per-household key, and no model-tier concept (ADR-007 leaves per-task model selection open;
/// the single <see cref="AiOptions.Model"/> is used as-is).
///
/// <para>The AI is an untrusted external function (ADR-007): the surface returns proposals and <b>cannot</b>
/// apply or mint a tag — that happens only through the user's tap in the editor (Gate 5). Every failure path
/// — an API error, an empty response, or malformed JSON — is a <em>soft</em> failure: it degrades to an
/// empty list (the editor renders no chips) and never throws into the caller.</para>
///
/// <para>Observability (Gate 9): each call is wrapped in an <see cref="Activity"/> span
/// (<c>recipe_tag_suggest</c>) carrying <c>ai.model</c> + token-usage attributes only — no ingredient names,
/// tag names, prompt, or API key is ever written to a log or span attribute.</para>
/// </summary>
public sealed class RecipeTagSuggester : IRecipeTagSuggester
{
    /// <summary>Upper bound on chips returned — keeps the suggestion row a glanceable nudge, not a wall.</summary>
    private const int MaxSuggestions = 6;

    private readonly ChatClient _chat;
    private readonly string _modelId;
    private readonly ILogger<RecipeTagSuggester> _logger;

    public RecipeTagSuggester(
        IOptions<AiOptions> options,
        ILogger<RecipeTagSuggester> logger)
    {
        _logger = logger;
        var ai = options.Value;
        _modelId = ai.Model;
        var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(ai.BaseUrl) };
        _chat = new OpenAIClient(new ApiKeyCredential(ai.ApiKey), clientOptions)
            .GetChatClient(ai.Model);
    }

    private const string SystemPrompt = """
        You suggest dietary/style tags for ONE home recipe, to help the cook keep their recipe library
        searchable. You are given the recipe's ingredient names and the household's existing tag vocabulary.
        Return ONLY valid JSON, no markdown.

        Output format:
        {
          "tags": [
            { "name": "tag copied verbatim from the vocabulary, or a new short tag", "category": "Diet | Protein | Flavor | Cuisine | null" }
          ]
        }

        Rules:
        - Strongly PREFER existing vocabulary tags: if an existing tag fits the ingredients, use its name
          verbatim (exact spelling). Only propose a NEW tag name when nothing in the vocabulary fits and the
          tag is clearly useful (e.g. a protein or diet stance the ingredients obviously support).
        - Suggest only tags a reasonable cook would confirm at a glance from the ingredient list — proteins
          present (e.g. Chicken, Beef), obvious diet stances (e.g. Vegetarian when no meat/fish appears), or
          a clear cuisine. Do NOT guess diet stances you cannot verify from the ingredients (e.g. never assert
          "dairy-free" — the cook confirms that, not you).
        - Return at most 6 tags, fewer is better. Return an empty "tags" array when nothing is confidently
          suggestable.
        - "category" is one of Diet, Protein, Flavor, Cuisine, or null. For an existing vocabulary tag you may
          copy its category or use null.
        - Never repeat a tag. Never output an ingredient name verbatim as a tag.
        """;

    public async Task<IReadOnlyList<TagSuggestion>> SuggestAsync(
        IReadOnlyList<string> ingredientNames,
        IReadOnlyList<TagVocabularyEntry> vocabulary,
        CancellationToken ct = default)
    {
        if (ingredientNames.Count == 0)
            return [];

        // Gate 9: span wraps the full AI call. Attributes: model id + token usage only — never ingredient
        // or tag content.
        using var activity = AiTelemetry.ActivitySource.StartActivity("recipe_tag_suggest");
        activity?.SetTag("ai.model", _modelId);

        var sw = Stopwatch.StartNew();
        _logger.LogInformation(
            "AI recipe-tag suggestion starting. Model: {Model}, Ingredients: {IngredientCount}, Vocabulary: {VocabCount}.",
            _modelId, ingredientNames.Count, vocabulary.Count);

        try
        {
            var userMessage = new UserChatMessage(BuildUserMessage(ingredientNames, vocabulary));

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
                    "AI recipe-tag suggestion returned an empty response. Model: {Model}, ElapsedMs: {ElapsedMs}.",
                    _modelId, sw.ElapsedMilliseconds);
                return [];
            }

            var suggestions = MapResponse(rawText, vocabulary);

            sw.Stop();
            _logger.LogInformation(
                "AI recipe-tag suggestion completed. Model: {Model}, Suggestions: {SuggestionCount}, ElapsedMs: {ElapsedMs}.",
                _modelId, suggestions.Count, sw.ElapsedMilliseconds);

            return suggestions;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex,
                "AI recipe-tag suggestion failed with exception. Model: {Model}, ElapsedMs: {ElapsedMs}.",
                _modelId, sw.ElapsedMilliseconds);
            return [];
        }
    }

    private static string BuildUserMessage(
        IReadOnlyList<string> ingredientNames, IReadOnlyList<TagVocabularyEntry> vocabulary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Recipe ingredients:");
        foreach (var name in ingredientNames)
            sb.Append("- ").AppendLine(name);
        sb.AppendLine();

        sb.AppendLine("Existing household tag vocabulary (prefer these — copy the name verbatim):");
        if (vocabulary.Count == 0)
        {
            sb.AppendLine("(none yet)");
        }
        else
        {
            foreach (var tag in vocabulary)
            {
                sb.Append("- ").Append(tag.Name);
                if (tag.Category is { } cat)
                    sb.Append(" (").Append(cat.ToDbValue()).Append(')');
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Maps the model's raw text response into <see cref="TagSuggestion"/>s. Strips markdown fences, parses
    /// the JSON, and enforces the untrusted-input contract (ADR-007): a suggested name that matches an
    /// existing vocabulary tag (case-insensitive) resolves to that tag's id + category (an existing pick);
    /// any other name becomes a NEW-tag proposal carrying the model's parsed category (unrecognised ⇒ null).
    /// Blank names are dropped, duplicates (by case-insensitive name) collapse to the first, and the list is
    /// capped at <see cref="MaxSuggestions"/>. Any malformed/empty content soft-fails to an empty list.
    /// Extracted as a pure static method so it is unit-testable against recorded fixtures with no live call.
    /// </summary>
    internal static IReadOnlyList<TagSuggestion> MapResponse(
        string? rawContent, IReadOnlyList<TagVocabularyEntry> vocabulary)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
            return [];

        // Case-insensitive name → existing vocabulary entry lookup (first wins on duplicate names).
        var byName = new Dictionary<string, TagVocabularyEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in vocabulary)
            byName.TryAdd(entry.Name.Trim(), entry);

        try
        {
            using var doc = JsonDocument.Parse(StripFences(rawContent));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tags", out var tagsEl)
                || tagsEl.ValueKind != JsonValueKind.Array)
                return [];

            var result = new List<TagSuggestion>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var el in tagsEl.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;

                var name = GetString(el, "name")?.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!seen.Add(name)) continue;

                if (byName.TryGetValue(name, out var existing))
                {
                    // Existing vocabulary tag — carry its id + category verbatim (the model's category is
                    // ignored for a match; the household's own category is authoritative).
                    result.Add(new TagSuggestion(existing.Name, existing.Category, existing.TagId));
                }
                else
                {
                    // A would-be new tag — parse the model's proposed category (unknown/absent ⇒ null).
                    result.Add(new TagSuggestion(name, ParseCategory(GetString(el, "category")), ExistingTagId: null));
                }

                if (result.Count >= MaxSuggestions) break;
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

    /// <summary>Maps the AI's string category label to the domain enum; unknown/absent/null ⇒ null.</summary>
    internal static TagCategory? ParseCategory(string? label) => label switch
    {
        "Diet" => TagCategory.Diet,
        "Protein" => TagCategory.Protein,
        "Flavor" => TagCategory.Flavor,
        "Cuisine" => TagCategory.Cuisine,
        _ => null,
    };

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static void RecordTokenUsage(Activity? activity, ChatTokenUsage? usage)
    {
        if (usage is null || activity is null) return;
        activity.SetTag("ai.usage.input_tokens", usage.InputTokenCount);
        activity.SetTag("ai.usage.output_tokens", usage.OutputTokenCount);
    }
}
