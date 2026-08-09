using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Plantry.Ai.Infrastructure;
using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Plantry.Planning.Infrastructure;
using Plantry.Tests.Unit.TestSupport;

namespace Plantry.Tests.Unit.MealPlanning.Infrastructure;

/// <summary>
/// L1 tests for <see cref="MealPlannerAiService"/>'s completion boundary — the part that sits behind the
/// concrete OpenAI <c>ChatClient</c> and is therefore invisible to the pure <c>MapResponse</c> mapper.
/// These drive <c>ProposeWeekAsync</c> through a scripted <c>ChatClient</c> seam (<see cref="ScriptedChatClient"/>)
/// injected via the adapter's internal test constructor (plantry-vnme), so the failure contract the mapper
/// never sees can be asserted directly (mirrors <c>GeminiReceiptParserCompletionTests</c> /
/// <c>RecipeTagSuggesterCompletionTests</c> / <c>DealMatcherChunkingTests</c>).
///
/// <para>
/// Covered: an empty slot set returns early without ever crossing the completion boundary; the happy path
/// issues exactly one completion and returns the mapped proposals; the outgoing prompt carries the system
/// prompt, the planning weights, and the candidate-recipe block (prompt construction); the caller's
/// cancellation token is forwarded; an API fault (any non-cancellation exception) soft-fails to an empty list
/// — the planner never throws into the application layer (ADR-007: AI output is a proposal, never a write); a
/// whitespace-only completion soft-fails the same way; an <see cref="OperationCanceledException"/> propagates
/// (the one exception the adapter must NOT swallow); and the Gate-9 telemetry span is set to
/// <see cref="ActivityStatusCode.Error"/> on a soft fail and carries the model + slot-count tags on success.
/// </para>
/// </summary>
public sealed class MealPlannerAiServiceCompletionTests
{
    private static readonly DateOnly SlotDate = new(2026, 6, 16);
    private static readonly Guid SlotGuid = Guid.Parse("0193b4a0-2222-7000-8000-000000000010");
    private static readonly Guid RecipeGuid = Guid.Parse("0193b4a0-2222-7000-8000-000000000020");
    private static readonly Guid AttendeeGuid = Guid.Parse("0193b4a0-2222-7000-8000-000000000030");

    private static readonly IReadOnlyList<PlannerMealSlotContext> Slots =
    [
        new(
            Date: SlotDate,
            MealSlotId: MealSlotId.From(SlotGuid),
            SlotLabel: "Dinner",
            EffectiveAttendees: [AttendeeGuid],
            Constraints: GenerationConstraints.Empty,
            CandidateRecipes:
            [
                new CandidateRecipe(
                    RecipeGuid, "Sheet-Pan Chicken", TagIds: [], DefaultServings: 4, CostPerServing: 3.20m,
                    HouseholdAvgRating: 4.3m, RatedCount: 3,
                    AttendeeStars: new Dictionary<Guid, int> { [AttendeeGuid] = 5 },
                    CostCompleteness: CandidateCostCompleteness.Complete,
                    FulfillmentPercent: 100,
                    HasContributingExpiringStock: true),
            ]),
    ];

    // A well-formed proposal for the single slot above — date + slot_id match the context key the mapper
    // validates against, and the dish carries a parseable recipe id (the mapper drops any proposal with no dishes).
    private static readonly string ValidResponse = $$"""
        [
          {
            "date": "2026-06-16",
            "slot_id": "{{SlotGuid}}",
            "dishes": [ { "recipe_id": "{{RecipeGuid}}", "servings": 4, "ordinal": 1 } ],
            "reasoning": "Uses expiring stock."
          }
        ]
        """;

    private static MealPlannerAiService Planner(ChatClient chat, string model = "test-model") =>
        new(chat, Options.Create(new AiOptions { Model = model }), NullLogger<MealPlannerAiService>.Instance);

    private static Task<IReadOnlyList<ProposedMeal>> Propose(
        MealPlannerAiService planner,
        IReadOnlyList<PlannerMealSlotContext>? slots = null,
        IReadOnlyList<PlannedMealSummary>? alreadyPlanned = null,
        CancellationToken ct = default) =>
        planner.ProposeWeekAsync(slots ?? Slots, alreadyPlanned ?? [], PlanningWeights.Default, ct);

    [Fact]
    public async Task No_Slots_Returns_Early_Without_Crossing_The_Completion_Boundary()
    {
        var chat = new ScriptedChatClient((_, _) => ScriptedChatClient.Completion(ValidResponse));

        var result = await Propose(Planner(chat), slots: []);

        Assert.Empty(result);
        Assert.Equal(0, chat.CallCount); // early return — the AI is never called for an empty week
    }

    [Fact]
    public async Task Happy_Path_Issues_Exactly_One_Completion_And_Returns_The_Mapped_Proposals()
    {
        var chat = new ScriptedChatClient((_, _) => ScriptedChatClient.Completion(ValidResponse));

        var result = await Propose(Planner(chat));

        Assert.Equal(1, chat.CallCount);
        var meal = Assert.Single(result);
        Assert.Equal(SlotDate, meal.Date);
        Assert.Equal(SlotGuid, meal.MealSlotId.Value);
        var dish = Assert.Single(meal.Dishes);
        Assert.Equal(RecipeGuid, dish.RecipeId);
    }

    [Fact]
    public async Task The_Outgoing_Prompt_Carries_The_System_Prompt_Weights_And_Candidate_Recipe_Block()
    {
        var chat = new ScriptedChatClient((_, _) => ScriptedChatClient.Completion(ValidResponse));

        await Propose(Planner(chat));

        var call = Assert.Single(chat.Calls);
        Assert.IsType<SystemChatMessage>(call.Messages[0]);            // system prompt sent first
        Assert.Contains(SlotGuid.ToString(), call.UserText);           // slot id, verbatim
        Assert.Contains(RecipeGuid.ToString(), call.UserText);         // candidate recipe id, verbatim
        Assert.Contains("Sheet-Pan Chicken", call.UserText);           // candidate recipe name
        Assert.Contains("waste=60, cost=20, variety=20", call.UserText); // planning weights forwarded
        Assert.Contains("attendee_ratings=[5]", call.UserText);        // attendee's own stars (plantry-zlwp.5)
        Assert.Contains("household_avg_rating=4.3 rated_by=3", call.UserText); // household fallback signal
        Assert.Contains("cost_completeness=complete", call.UserText);
        Assert.Contains("fulfillment=100%", call.UserText);
        Assert.Contains("expiring_stock=use_soon", call.UserText);
    }

    [Fact]
    public async Task The_System_Prompt_Constrains_Evidence_Based_Reasoning_And_Uses_A_Neutral_Example()
    {
        var chat = new ScriptedChatClient((_, _) => ScriptedChatClient.Completion(ValidResponse));

        await Propose(Planner(chat));

        var call = Assert.Single(chat.Calls);
        var systemText = Assert.IsType<SystemChatMessage>(call.Messages[0]).Content[0].Text;
        var normalizedSystemText = string.Join(
            " ",
            systemText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains(
            "make a waste claim only when the selected candidate has expiring_stock=use_soon",
            normalizedSystemText);
        Assert.Contains(
            "make a cost claim only when that candidate has cost_completeness=complete",
            normalizedSystemText);
        Assert.Contains("use a neutral rationale", normalizedSystemText);
        Assert.Contains("Fits the slot constraints.", normalizedSystemText);
        Assert.DoesNotContain("High fulfillment from expiring stock.", normalizedSystemText);
    }

    [Fact]
    public async Task Already_Planned_Section_Is_Omitted_When_The_List_Is_Empty()
    {
        var chat = new ScriptedChatClient((_, _) => ScriptedChatClient.Completion(ValidResponse));

        await Propose(Planner(chat), alreadyPlanned: []);

        var call = Assert.Single(chat.Calls);
        Assert.DoesNotContain("Already planned this week", call.UserText);
    }

    [Fact]
    public async Task Already_Planned_Section_Lists_Date_Slot_Label_And_Dish_Names_When_Supplied()
    {
        var chat = new ScriptedChatClient((_, _) => ScriptedChatClient.Completion(ValidResponse));

        // Values chosen to appear nowhere else in the prompt (the "Dinner" slot and
        // "Sheet-Pan Chicken" candidate already appear in the Slots fixture above), so this
        // assertion actually pins the already-planned line's format rather than passing
        // vacuously against text emitted by the "Meal slots to fill" section.
        var alreadyPlanned = new List<PlannedMealSummary>
        {
            new(new DateOnly(2026, 6, 15), "Brunch", ["Leftover Chili", "Garlic Bread"]),
        };

        await Propose(Planner(chat), alreadyPlanned: alreadyPlanned);

        var call = Assert.Single(chat.Calls);
        Assert.Contains("Already planned this week", call.UserText);
        Assert.Contains("2026-06-15 Brunch: Leftover Chili, Garlic Bread", call.UserText);
    }

    [Fact]
    public async Task The_Caller_CancellationToken_Is_Forwarded_To_The_Completion()
    {
        using var cts = new CancellationTokenSource();
        var chat = new ScriptedChatClient((_, _) => ScriptedChatClient.Completion(ValidResponse));

        await Propose(Planner(chat), ct: cts.Token);

        var call = Assert.Single(chat.Calls);
        Assert.Equal(cts.Token, call.CancellationToken);
    }

    [Fact]
    public async Task An_Api_Fault_Soft_Fails_To_An_Empty_List_Without_Throwing()
    {
        var chat = new ScriptedChatClient((_, _) => throw new InvalidOperationException("gateway 500"));

        var result = await Propose(Planner(chat));

        Assert.Empty(result);
        Assert.Equal(1, chat.CallCount); // the completion was attempted before it faulted
    }

    [Fact]
    public async Task An_Empty_Response_Soft_Fails_To_An_Empty_List()
    {
        var chat = new ScriptedChatClient((_, _) => ScriptedChatClient.Completion("   ")); // whitespace-only

        var result = await Propose(Planner(chat));

        Assert.Empty(result);
        Assert.Equal(1, chat.CallCount);
    }

    [Fact]
    public async Task An_OperationCanceledException_Propagates_Out_Of_ProposeWeekAsync()
    {
        // The adapter's catch filter excludes OCE — cancellation must surface, not soft-fail to an empty list.
        var chat = new ScriptedChatClient((_, _) => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => Propose(Planner(chat)));
    }

    [Fact]
    public async Task A_Soft_Failed_Proposal_Sets_The_Telemetry_Span_Status_To_Error()
    {
        var spans = AiSpanCapture.Capture("meal_plan_propose", out var listener);
        using (listener)
        {
            var chat = new ScriptedChatClient((_, _) => throw new InvalidOperationException("gateway 500"));
            await Propose(Planner(chat));
        }

        var span = Assert.Single(spans);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task An_Empty_Response_Sets_The_Telemetry_Span_Status_To_Error()
    {
        var spans = AiSpanCapture.Capture("meal_plan_propose", out var listener);
        using (listener)
        {
            var chat = new ScriptedChatClient((_, _) => ScriptedChatClient.Completion("   "));
            await Propose(Planner(chat));
        }

        var span = Assert.Single(spans);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task A_Successful_Proposal_Tags_The_Span_With_Model_And_Slot_Count_And_Leaves_Status_Unset()
    {
        var spans = AiSpanCapture.Capture("meal_plan_propose", out var listener);
        using (listener)
        {
            var chat = new ScriptedChatClient((_, _) => ScriptedChatClient.Completion(ValidResponse));
            await Propose(Planner(chat));
        }

        var span = Assert.Single(spans);
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.Equal("test-model", span.GetTagItem("ai.model"));
        Assert.Equal(1, span.GetTagItem("ai.meal_plan.slot_count"));
    }

    // plantry-df6p: RecordTokenUsage is now shared across all six adapters — this proves MealPlannerAiService
    // wires its OWN AiFunction (meal_plan_propose) and model id into the ai.usage.tokens metric, catching a
    // copy-paste slip (e.g. the wrong AiFunction constant) that would otherwise compile and pass silently. A
    // unique per-test model id is the discriminator: the meter is process-global and xUnit runs test classes
    // in parallel, so filtering only by instrument name would pick up other adapter tests' emissions.
    [Fact]
    public async Task A_Successful_Proposal_Emits_The_Tokens_Metric_Tagged_With_Its_Own_Function_And_Model()
    {
        const string model = "meal-plan-propose-usage-sentinel";
        var measurements = TokenUsageMeasurementCapture.Capture(model, out var listener);
        using (listener)
        {
            var usage = ScriptedChatClient.Usage(inputTokens: 640, outputTokens: 96);
            var chat = new ScriptedChatClient((_, _) => ScriptedChatClient.Completion(ValidResponse, usage));

            await Propose(Planner(chat, model: model));
        }

        Assert.Contains(measurements, m => m is (640, AiFunction.MealPlanPropose, "input"));
        Assert.Contains(measurements, m => m is (96, AiFunction.MealPlanPropose, "output"));
    }
}
