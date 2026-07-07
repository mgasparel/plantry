using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Plantry.Ai.Infrastructure;
using Plantry.Recipes.Infrastructure;
using Plantry.Tests.Unit.TestSupport;

namespace Plantry.Tests.Unit.Recipes.Infrastructure;

/// <summary>
/// L1 tests for <see cref="IngredientConversionInferrer"/>'s completion boundary — the part that sits behind
/// the concrete OpenAI <c>ChatClient</c> and is therefore invisible to
/// <see cref="IngredientConversionInferrerTests"/> (which cover only the pure <c>ParseFactor</c> mapper).
/// These drive <c>InferFactorAsync</c> through a scripted <c>ChatClient</c> seam
/// (<see cref="ScriptedChatClient"/>) injected via the adapter's internal test constructor, so the failure
/// contract the mapper never sees can be asserted directly (mirrors <c>DealMatcherChunkingTests</c>).
///
/// <para>
/// Covered: the happy path issues exactly one completion and returns the parsed factor; an API fault (any
/// non-cancellation exception) soft-fails to <c>null</c> (no seed) without throwing into the caller; a
/// whitespace-only completion soft-fails the same way; and an <see cref="OperationCanceledException"/>
/// propagates (the one exception the adapter must NOT swallow). The blank-argument guard is proven to
/// short-circuit before the completion boundary is ever crossed.
/// </para>
/// </summary>
public sealed class IngredientConversionInferrerCompletionTests
{
    private static IngredientConversionInferrer Inferrer(ChatClient chat) =>
        new(
            chat,
            Options.Create(new AiOptions { Model = "test-model" }),
            NullLogger<IngredientConversionInferrer>.Instance);

    [Fact]
    public async Task Happy_Path_Issues_Exactly_One_Completion_And_Returns_The_Parsed_Factor()
    {
        var chat = new ScriptedChatClient((_, _) => ScriptedChatClient.Completion("""{ "factor": 120 }"""));
        var inferrer = Inferrer(chat);

        var factor = await inferrer.InferFactorAsync("cashews", "cup", "g");

        Assert.Equal(120m, factor);
        Assert.Equal(1, chat.CallCount);
    }

    [Fact]
    public async Task An_Api_Fault_Soft_Fails_To_Null_Without_Throwing()
    {
        var chat = new ScriptedChatClient((_, _) => throw new InvalidOperationException("gateway 500"));
        var inferrer = Inferrer(chat);

        var factor = await inferrer.InferFactorAsync("cashews", "cup", "g");

        Assert.Null(factor);
        Assert.Equal(1, chat.CallCount); // the completion was attempted before it faulted
    }

    [Fact]
    public async Task An_Empty_Response_Soft_Fails_To_Null()
    {
        var chat = new ScriptedChatClient((_, _) => ScriptedChatClient.Completion("   ")); // whitespace-only
        var inferrer = Inferrer(chat);

        var factor = await inferrer.InferFactorAsync("cashews", "cup", "g");

        Assert.Null(factor);
        Assert.Equal(1, chat.CallCount);
    }

    [Fact]
    public async Task An_OperationCanceledException_Propagates_Out_Of_InferFactorAsync()
    {
        // The adapter's catch filter excludes OCE — cancellation must surface, not soft-fail to null.
        var chat = new ScriptedChatClient((_, _) => throw new OperationCanceledException());
        var inferrer = Inferrer(chat);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => inferrer.InferFactorAsync("cashews", "cup", "g"));
    }

    [Fact]
    public async Task A_Blank_Argument_Short_Circuits_Before_Any_Completion()
    {
        var chat = new ScriptedChatClient((_, _) => throw new InvalidOperationException("must not be called"));
        var inferrer = Inferrer(chat);

        var factor = await inferrer.InferFactorAsync("cashews", "   ", "g");

        Assert.Null(factor);
        Assert.Equal(0, chat.CallCount);
    }
}
