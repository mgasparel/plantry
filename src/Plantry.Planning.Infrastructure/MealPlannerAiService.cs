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
using Plantry.Planning.Application;
using Plantry.Planning.Domain;

namespace Plantry.Planning.Infrastructure;

/// <summary>
/// <see cref="IMealPlanner"/> over an OpenAI-compatible <c>ChatClient</c> (same provider as
/// <c>GeminiReceiptParser</c> — OpenRouter + Gemini by default, see <see cref="AiOptions"/>).
///
/// The AI is an untrusted external function (ADR-007): output is a proposal, never a write.
/// Any API or parse failure is a soft failure — returns an empty list, never throws.
/// The <see cref="MapResponse"/> static method is extracted for testability against recorded fixtures.
///
/// Observability (Gate 9): each call is wrapped in an <see cref="Activity"/> span
/// (<c>meal_plan_propose</c>) with <c>ai.model</c>, <c>ai.usage.input_tokens</c>, and
/// <c>ai.usage.output_tokens</c> attributes. Failures set the span to
/// <see cref="ActivityStatusCode.Error"/> and emit a <c>LogError</c>. No slot content, user ids,
/// or API key is written to any log or span attribute.
/// </summary>
public sealed class MealPlannerAiService : IMealPlanner
{
    private readonly ChatClient _chat;
    private readonly string _modelId;
    private readonly ILogger<MealPlannerAiService> _logger;

    public MealPlannerAiService(
        IOptions<AiOptions> options,
        ILogger<MealPlannerAiService> logger)
        : this(CreateClient(options.Value), options, logger)
    {
    }

    // Test seam (plantry-vnme): lets unit tests script the completion boundary so the soft-fail (API error /
    // empty response), cancellation-propagation, telemetry-span, and prompt-construction paths — which sit
    // behind the concrete ChatClient and are invisible to the pure MapResponse mapper — can be asserted
    // directly. Production always routes through the public ctor above, which builds the real client and
    // delegates here — no behaviour, public-API, or DI change (mirrors DealMatcher/GeminiReceiptParser).
    internal MealPlannerAiService(
        ChatClient chat,
        IOptions<AiOptions> options,
        ILogger<MealPlannerAiService> logger)
    {
        _logger = logger;
        _chat = chat;
        _modelId = options.Value.Model;
    }

    private static ChatClient CreateClient(AiOptions ai) =>
        new OpenAIClient(new ApiKeyCredential(ai.ApiKey), new OpenAIClientOptions { Endpoint = new Uri(ai.BaseUrl) })
            .GetChatClient(ai.Model);

    private const string SystemPrompt = """
        You are a meal planning assistant. Given a set of empty meal slots for a week, propose one
        recipe for each slot from the provided candidate list. Follow all hard constraints strictly.

        Rules:
        - NEVER propose a recipe whose tag IDs appear in the restricted_tag_ids list for that slot.
        - ALWAYS prefer recipes that include all required_tag_ids for that slot.
        - Use preferred_tag_weights (positive = preferred, negative = disliked) as soft guidance.
        - Use attendee_ratings and household_avg_rating as soft guidance, never a hard filter: favour
          recipes rated highly by THIS slot's attendees (attendee_ratings, 1-5 stars each); when an
          attendee has not rated, fall back to household_avg_rating for that recipe. A low individual
          rating should weigh against proposing that recipe for the slot but must never exclude it
          outright — a recipe with no rating data at all is judged purely on its other merits.
        - Use the planning weights (waste/cost/variety) to prioritise. Waste evidence is present only when
          expiring_stock=use_soon: a positive amount was allocated from on-hand stock with an expiry today
          or later inside the horizon. Mere on-hand stock, an expired lot, or a generic fulfillment score
          is not waste evidence. Cost evidence is exact only when cost_completeness=complete; partial cost
          is an under-estimate and unknown cost is unresolved. Never treat partial or unknown cost as zero
          or as cheaper than a recipe with complete evidence. Higher variety weight means avoid repeating
          the same recipe across the week.
        - In each proposal's reasoning, make a waste claim only when the selected candidate has
          expiring_stock=use_soon, and make a cost claim only when that candidate has
          cost_completeness=complete. For expiring_stock=none or unknown, or cost_completeness=partial
          or unknown, use a neutral rationale and do not imply an unsupported waste or cost benefit.
        - The "Already planned this week" list (when present) shows meals the household has already
          planned this week. Treat them as part of the week when applying the variety weight — do not
          unintentionally repeat an already-planned dish. Repeating one is acceptable when the weights
          and constraints clearly favour it; explain the repeat in the reasoning.
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
            "reasoning": "Fits the slot constraints."
          }
        ]
        """;

    public async Task<IReadOnlyList<ProposedMeal>> ProposeWeekAsync(
        IReadOnlyList<PlannerMealSlotContext> slotsContext,
        IReadOnlyList<PlannedMealSummary> alreadyPlanned,
        PlanningWeights weights,
        CancellationToken ct = default)
    {
        if (slotsContext.Count == 0) return [];

        // Gate 9: span wraps the full AI call (latency-sensitive, most likely failure point).
        // Attributes: model id and token usage only — no slot content, user ids, or API key.
        using var activity = AiTelemetry.ActivitySource.StartActivity(AiFunction.MealPlanPropose);
        activity?.SetTag("ai.model", _modelId);
        activity?.SetTag("ai.meal_plan.slot_count", slotsContext.Count);

        var sw = Stopwatch.StartNew();
        _logger.LogInformation(
            "AI meal plan proposal starting. Model: {Model}, Slots: {SlotCount}.",
            _modelId, slotsContext.Count);

        try
        {
            var userMessage = BuildUserMessage(slotsContext, alreadyPlanned, weights);
            var response = await _chat.CompleteChatAsync(
                [new SystemChatMessage(SystemPrompt), new UserChatMessage(userMessage)],
                cancellationToken: ct);

            var completion = response.Value;
            AiUsageTelemetry.RecordTokenUsage(activity, completion.Usage, AiFunction.MealPlanPropose, _modelId);

            var rawText = completion.Content.Count > 0 ? completion.Content[0].Text : null;

            // Gate 9: empty response is a soft failure but must surface as an error span + log.
            if (string.IsNullOrWhiteSpace(rawText))
            {
                activity?.SetStatus(ActivityStatusCode.Error, "AI returned an empty response.");
                _logger.LogError(
                    "AI meal plan proposal returned an empty response. Model: {Model}, ElapsedMs: {ElapsedMs}.",
                    _modelId, sw.ElapsedMilliseconds);
                return [];
            }

            var proposals = MapResponse(rawText, slotsContext);

            sw.Stop();
            _logger.LogInformation(
                "AI meal plan proposal completed. Model: {Model}, Slots: {SlotCount}, Proposals: {ProposalCount}, ElapsedMs: {ElapsedMs}.",
                _modelId, slotsContext.Count, proposals.Count, sw.ElapsedMilliseconds);

            return proposals;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex,
                "AI meal plan proposal failed with exception. Model: {Model}, ElapsedMs: {ElapsedMs}.",
                _modelId, sw.ElapsedMilliseconds);
            return [];
        }
    }

    private static string BuildUserMessage(
        IReadOnlyList<PlannerMealSlotContext> contexts,
        IReadOnlyList<PlannedMealSummary> alreadyPlanned,
        PlanningWeights weights)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Planning weights: waste={weights.Waste}, cost={weights.Cost}, variety={weights.Variety}");
        sb.AppendLine();

        // plantry-6mux: soft variety context — omit the section entirely when nothing is planned yet
        // so the prompt shape for an empty week matches the pre-existing behaviour exactly.
        if (alreadyPlanned.Count > 0)
        {
            sb.AppendLine("Already planned this week:");
            foreach (var meal in alreadyPlanned)
                sb.AppendLine($"  - {meal.Date:yyyy-MM-dd} {meal.SlotLabel}: {string.Join(", ", meal.DishNames)}");
            sb.AppendLine();
        }

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
                var biases = string.Join(", ", ctx.Constraints.PreferredTagWeights.Select(kv => FormattableString.Invariant($"{kv.Key}:{kv.Value:F2}")));
                sb.AppendLine($"  preferred_tag_weights: {{{biases}}}");
            }

            sb.AppendLine($"  candidate_recipes ({ctx.CandidateRecipes.Count}):");
            foreach (var r in ctx.CandidateRecipes)
            {
                var tags = r.TagIds.Count > 0 ? $" tags=[{string.Join(",", r.TagIds)}]" : "";
                var cost = r.CostPerServing.HasValue
                    ? FormattableString.Invariant($" cost={r.CostPerServing:F2}")
                    : " cost=unknown";
                var costCompleteness = $" cost_completeness={r.CostCompleteness.ToString().ToLowerInvariant()}";
                var fulfillment = r.FulfillmentPercent.HasValue
                    ? $" fulfillment={r.FulfillmentPercent.Value}%"
                    : " fulfillment=unknown";
                var expiringStock = r.HasContributingExpiringStock switch
                {
                    true => " expiring_stock=use_soon",
                    false => " expiring_stock=none",
                    null => " expiring_stock=unknown",
                };
                // plantry-zlwp.5: attendee_ratings lists THIS slot's attendees' own stars (identity-free —
                // the AI reasons over the values, not who gave them); household_avg_rating/rated_by are
                // the household-wide fallback signal. Both soft guidance only — see SystemPrompt rules.
                var attendeeRatings = r.AttendeeStars is { Count: > 0 }
                    ? $" attendee_ratings=[{string.Join(",", r.AttendeeStars.Values)}]"
                    : "";
                var householdRating = r.HouseholdAvgRating.HasValue
                    ? FormattableString.Invariant($" household_avg_rating={r.HouseholdAvgRating:F1} rated_by={r.RatedCount}")
                    : "";
                sb.AppendLine($"    - [{r.RecipeId}] {r.Name} (servings={r.DefaultServings}{tags}{cost}{costCompleteness}{fulfillment}{expiringStock}{attendeeRatings}{householdRating})");
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
