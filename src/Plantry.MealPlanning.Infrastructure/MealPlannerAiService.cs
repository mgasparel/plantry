using System.ClientModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using Plantry.Intake.Infrastructure;
using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;

namespace Plantry.MealPlanning.Infrastructure;

/// <summary>
/// <see cref="IMealPlanner"/> over an OpenAI-compatible <c>ChatClient</c> (same provider as
/// <c>GeminiReceiptParser</c> — OpenRouter + Gemini by default, see <see cref="AiOptions"/>).
///
/// The AI is an untrusted external function (ADR-007): output is a proposal, never a write.
/// Any API or parse failure is a soft failure — returns an empty list, never throws.
/// The <see cref="MapResponse"/> static method is extracted for testability against recorded fixtures.
/// </summary>
public sealed class MealPlannerAiService : IMealPlanner
{
    private readonly ChatClient _chat;

    public MealPlannerAiService(IOptions<AiOptions> options)
    {
        var ai = options.Value;
        var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(ai.BaseUrl) };
        _chat = new OpenAIClient(new ApiKeyCredential(ai.ApiKey), clientOptions)
            .GetChatClient(ai.Model);
    }

    private const string SystemPrompt = """
        You are a meal planning assistant. Given a set of empty meal slots for a week, propose one
        recipe for each slot from the provided candidate list. Follow all hard constraints strictly.

        Rules:
        - NEVER propose a recipe whose tag IDs appear in the restricted_tag_ids list for that slot.
        - ALWAYS prefer recipes that include all required_tag_ids for that slot.
        - Use preferred_tag_weights (positive = preferred, negative = disliked) as soft guidance.
        - Use the planning weights (waste/cost/variety) to prioritise: higher waste weight means
          prefer recipes that use ingredients already on hand; higher cost weight means prefer cheaper
          recipes; higher variety weight means avoid repeating the same recipe across the week.
        - Choose only recipe_ids from the candidate_recipes list. Do not invent new recipe IDs.
        - Set servings to the recipe's default_servings unless you have a strong reason to differ.
        - Provide a short reasoning (1-2 sentences) for each proposal.

        Output format — return ONLY valid JSON, no markdown fences:
        [
          {
            "date": "2026-06-16",
            "slot_id": "uuid-of-slot",
            "dishes": [
              { "recipe_id": "uuid-of-recipe", "servings": 4, "ordinal": 1 }
            ],
            "reasoning": "High fulfillment from expiring stock."
          }
        ]
        """;

    public async Task<IReadOnlyList<ProposedMeal>> ProposeWeekAsync(
        IReadOnlyList<PlannerMealSlotContext> slotsContext,
        PlanningWeights weights,
        CancellationToken ct = default)
    {
        if (slotsContext.Count == 0) return [];

        try
        {
            var userMessage = BuildUserMessage(slotsContext, weights);
            var response = await _chat.CompleteChatAsync(
                [new SystemChatMessage(SystemPrompt), new UserChatMessage(userMessage)],
                cancellationToken: ct);

            return MapResponse(response.Value.Content[0].Text, slotsContext);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Soft failure: log but return empty (never throw into the page)
            return [];
        }
    }

    private static string BuildUserMessage(
        IReadOnlyList<PlannerMealSlotContext> contexts,
        PlanningWeights weights)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Planning weights: waste={weights.Waste}, cost={weights.Cost}, variety={weights.Variety}");
        sb.AppendLine();
        sb.AppendLine("Meal slots to fill:");
        sb.AppendLine();

        foreach (var ctx in contexts)
        {
            sb.AppendLine($"Slot: {ctx.SlotLabel} on {ctx.Date:yyyy-MM-dd} (slot_id: {ctx.MealSlotId.Value})");
            sb.AppendLine($"  Attendees: {ctx.EffectiveAttendees.Count}");

            if (ctx.Constraints.RequiredTagIds.Count > 0)
                sb.AppendLine($"  required_tag_ids: [{string.Join(", ", ctx.Constraints.RequiredTagIds)}]");
            if (ctx.Constraints.RestrictedTagIds.Count > 0)
                sb.AppendLine($"  restricted_tag_ids: [{string.Join(", ", ctx.Constraints.RestrictedTagIds)}]");
            if (ctx.Constraints.PreferredTagWeights.Count > 0)
            {
                var biases = string.Join(", ", ctx.Constraints.PreferredTagWeights.Select(kv => $"{kv.Key}:{kv.Value:F2}"));
                sb.AppendLine($"  preferred_tag_weights: {{{biases}}}");
            }

            sb.AppendLine($"  candidate_recipes ({ctx.CandidateRecipes.Count}):");
            foreach (var r in ctx.CandidateRecipes)
            {
                var tags = r.TagIds.Count > 0 ? $" tags=[{string.Join(",", r.TagIds)}]" : "";
                var cost = r.CostPerServing.HasValue ? $" cost={r.CostPerServing:F2}" : "";
                sb.AppendLine($"    - [{r.RecipeId}] {r.Name} (servings={r.DefaultServings}{tags}{cost})");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Maps the model's raw text response to a list of <see cref="ProposedMeal"/>s. Strips markdown
    /// fences, parses JSON, and maps to domain objects. Any malformed content soft-fails to empty list.
    /// Extracted as a pure static method for unit-testability against recorded fixtures.
    /// </summary>
    internal static IReadOnlyList<ProposedMeal> MapResponse(
        string? rawContent,
        IReadOnlyList<PlannerMealSlotContext> contexts)
    {
        if (string.IsNullOrWhiteSpace(rawContent)) return [];

        // Build a lookup by slot key for validation
        var contextMap = contexts.ToDictionary(
            c => $"{c.Date:yyyy-MM-dd}_{c.MealSlotId.Value:N}",
            c => c);

        try
        {
            var json = StripFences(rawContent);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

            var results = new List<ProposedMeal>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var dateStr = GetString(el, "date");
                var slotIdStr = GetString(el, "slot_id");
                var reasoning = GetString(el, "reasoning");

                if (!DateOnly.TryParse(dateStr, out var date)) continue;
                if (!Guid.TryParse(slotIdStr, out var slotGuid)) continue;

                var slotId = MealSlotId.From(slotGuid);
                var cellKey = $"{date:yyyy-MM-dd}_{slotId.Value:N}";

                // Only accept proposals for slots we actually asked about
                if (!contextMap.TryGetValue(cellKey, out var ctx)) continue;

                var dishes = new List<ProposedDish>();
                if (el.TryGetProperty("dishes", out var dishesEl) && dishesEl.ValueKind == JsonValueKind.Array)
                {
                    var ordinal = 0;
                    foreach (var dishEl in dishesEl.EnumerateArray())
                    {
                        var recipeIdStr = GetString(dishEl, "recipe_id");
                        if (!Guid.TryParse(recipeIdStr, out var recipeGuid)) continue;

                        var servings = GetInt(dishEl, "servings") ?? 1;
                        var explicitOrdinal = GetInt(dishEl, "ordinal") ?? ++ordinal;
                        dishes.Add(new ProposedDish(recipeGuid, Math.Max(1, servings), explicitOrdinal));
                    }
                }

                if (dishes.Count == 0) continue;

                results.Add(new ProposedMeal(
                    Date: date,
                    MealSlotId: slotId,
                    EffectiveAttendees: ctx.EffectiveAttendees,
                    Dishes: dishes,
                    Reasoning: reasoning));
            }

            return results;
        }
        catch (JsonException)
        {
            return [];
        }
    }

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

    private static int? GetInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;
}
