using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Plantry.Ai.Infrastructure;
using Plantry.Recipes.Infrastructure;
using Plantry.Tests.Unit.TestSupport;

namespace Plantry.Tests.Unit.Recipes.Infrastructure;

/// <summary>
/// L1 tests for <see cref="DietTagContradictionChecker"/>'s completion boundary — the part that sits behind
/// the concrete OpenAI <c>ChatClient</c> and is therefore invisible to <see cref="DietTagContradictionCheckerTests"/>
/// (which cover only the pure <c>MapResponse</c> mapper). These drive <c>CheckAsync</c> through a scripted
/// <c>ChatClient</c> seam (<see cref="ScriptedChatClient"/>) injected via the adapter's internal test
/// constructor (plantry-df6p — the same seam <c>GeminiReceiptParser</c>/<c>DealMatcher</c>/<c>RecipeTagSuggester</c>
/// already use).
///
/// <para>
/// Covered: the checker wires its OWN <see cref="AiFunction"/> (<c>recipe_diet_nudge</c>) and model id into
/// the shared <see cref="AiUsageTelemetry.RecordTokenUsage"/> helper's <c>ai.usage.tokens</c> metric — the
/// behaviour plantry-df6p ships. Nothing before this ticket exercised <c>CheckAsync</c> through a scripted
/// completion at all.
/// </para>
/// </summary>
public sealed class DietTagContradictionCheckerCompletionTests
{
    private static readonly IReadOnlyList<string> Ingredients = ["Parmesan", "Rigatoni", "Garlic"];
    private static readonly IReadOnlyList<string> DietTags = ["Dairy-Free", "Vegetarian"];

    private static DietTagContradictionChecker Checker(ChatClient chat, string model = "test-model") =>
        new(
            chat,
            Options.Create(new AiOptions { Model = model }),
            NullLogger<DietTagContradictionChecker>.Instance);

    [Fact]
    public async Task Happy_Path_Issues_Exactly_One_Completion_And_Returns_The_Mapped_Contradictions()
    {
        var chat = new ScriptedChatClient((_, _) => ScriptedChatClient.Completion(
            """{ "contradictions": [ { "ingredient": "Parmesan", "tag": "Dairy-Free" } ] }"""));
        var checker = Checker(chat);

        var result = await checker.CheckAsync(Ingredients, DietTags);

        Assert.Equal(1, chat.CallCount);
        var c = Assert.Single(result);
        Assert.Equal("Parmesan", c.IngredientName);
    }

    [Fact]
    public async Task An_Api_Fault_Soft_Fails_To_An_Empty_List_Without_Throwing()
    {
        var chat = new ScriptedChatClient((_, _) => throw new InvalidOperationException("gateway 500"));
        var checker = Checker(chat);

        var result = await checker.CheckAsync(Ingredients, DietTags);

        Assert.Empty(result);
        Assert.Equal(1, chat.CallCount); // the completion was attempted before it faulted
    }

    [Fact]
    public async Task An_Empty_Response_Soft_Fails_To_An_Empty_List()
    {
        var chat = new ScriptedChatClient((_, _) => ScriptedChatClient.Completion("   ")); // whitespace-only
        var checker = Checker(chat);

        var result = await checker.CheckAsync(Ingredients, DietTags);

        Assert.Empty(result);
        Assert.Equal(1, chat.CallCount);
    }

    [Fact]
    public async Task An_OperationCanceledException_Propagates_Out_Of_CheckAsync()
    {
        // The adapter's catch filter excludes OCE — cancellation must surface, not soft-fail to an empty list.
        var chat = new ScriptedChatClient((_, _) => throw new OperationCanceledException());
        var checker = Checker(chat);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => checker.CheckAsync(Ingredients, DietTags));
    }

    [Fact]
    public async Task No_Ingredients_Or_No_Diet_Tags_Short_Circuits_Before_Any_Completion()
    {
        var chat = new ScriptedChatClient((_, _) => throw new InvalidOperationException("must not be called"));
        var checker = Checker(chat);

        Assert.Empty(await checker.CheckAsync([], DietTags));
        Assert.Empty(await checker.CheckAsync(Ingredients, []));
        Assert.Equal(0, chat.CallCount);
    }

    // plantry-df6p: RecordTokenUsage is now shared across all six adapters — this proves
    // DietTagContradictionChecker wires its OWN AiFunction (recipe_diet_nudge) and model id into the
    // ai.usage.tokens metric, catching a copy-paste slip (e.g. the wrong AiFunction constant) that would
    // otherwise compile and pass silently. A unique per-test model id is the discriminator: the meter is
    // process-global and xUnit runs test classes in parallel, so filtering only by instrument name would
    // pick up other adapter tests' emissions.
    [Fact]
    public async Task A_Successful_Check_Emits_The_Tokens_Metric_Tagged_With_Its_Own_Function_And_Model()
    {
        const string model = "recipe-diet-nudge-usage-sentinel";
        var measurements = TokenUsageMeasurementCapture.Capture(model, out var listener);
        using (listener)
        {
            var usage = ScriptedChatClient.Usage(inputTokens: 55, outputTokens: 15);
            var chat = new ScriptedChatClient((_, _) => ScriptedChatClient.Completion(
                """{ "contradictions": [ { "ingredient": "Parmesan", "tag": "Dairy-Free" } ] }""", usage));
            var checker = Checker(chat, model: model);

            await checker.CheckAsync(Ingredients, DietTags);
        }

        Assert.Contains(measurements, m => m is (55, AiFunction.RecipeDietNudge, "input"));
        Assert.Contains(measurements, m => m is (15, AiFunction.RecipeDietNudge, "output"));
    }
}
