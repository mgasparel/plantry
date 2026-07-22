using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Plantry.Ai.Infrastructure;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;
using Plantry.Tests.Unit.TestSupport;

namespace Plantry.Tests.Unit.Recipes.Infrastructure;

/// <summary>
/// L1 tests for <see cref="RecipeTagSuggester"/>'s completion boundary — the part that sits behind the
/// concrete OpenAI <c>ChatClient</c> and is therefore invisible to <see cref="RecipeTagSuggesterTests"/>
/// (which cover only the pure <c>MapResponse</c> mapper). These drive <c>SuggestAsync</c> through a scripted
/// <c>ChatClient</c> seam (<see cref="ScriptedChatClient"/>) injected via the adapter's internal test
/// constructor, so the failure contract the mapper never sees can be asserted directly (mirrors
/// <c>DealMatcherChunkingTests</c>).
///
/// <para>
/// Covered: the happy path issues exactly one completion and returns the mapped suggestions; an API fault
/// (any non-cancellation exception) soft-fails to an empty list — the editor renders no chips and nothing
/// throws into the caller; a whitespace-only completion soft-fails the same way; and an
/// <see cref="OperationCanceledException"/> propagates (the one exception the adapter must NOT swallow). The
/// empty-ingredients guard is proven to short-circuit before the completion boundary is ever crossed.
/// </para>
/// </summary>
public sealed class RecipeTagSuggesterCompletionTests
{
    private static readonly Guid ChickenTagId = Guid.Parse("0193b4a0-2222-7000-8000-000000000001");

    private static readonly IReadOnlyList<TagVocabularyEntry> Vocabulary =
    [
        new(ChickenTagId, "Chicken", TagCategory.Protein),
    ];

    private static readonly IReadOnlyList<string> Ingredients = ["chicken thighs", "cream"];
    private static readonly IReadOnlyList<string> NoAppliedTags = [];

    private static RecipeTagSuggester Suggester(ChatClient chat) =>
        new(
            chat,
            Options.Create(new AiOptions { Model = "test-model" }),
            NullLogger<RecipeTagSuggester>.Instance);

    [Fact]
    public async Task Happy_Path_Issues_Exactly_One_Completion_And_Returns_The_Mapped_Suggestions()
    {
        var chat = new ScriptedChatClient((_, _) => ScriptedChatClient.Completion(
            """{ "tags": [ { "name": "Chicken", "category": "Protein" } ] }"""));
        var suggester = Suggester(chat);

        var result = await suggester.SuggestAsync(Ingredients, Vocabulary, NoAppliedTags);

        Assert.Equal(1, chat.CallCount);
        var s = Assert.Single(result);
        Assert.Equal(ChickenTagId, s.ExistingTagId);
    }

    // plantry-crre: the applied-tag names go into the outgoing prompt so the model can avoid proposing a
    // tag redundant with (or a subset already implied by) one already on the recipe.
    [Fact]
    public async Task Applied_Tag_Names_Are_Included_In_The_Outgoing_Prompt()
    {
        var chat = new ScriptedChatClient((_, _) => ScriptedChatClient.Completion("""{ "tags": [] }"""));
        var suggester = Suggester(chat);

        await suggester.SuggestAsync(Ingredients, Vocabulary, ["Vegan"]);

        Assert.Equal(1, chat.CallCount);
        Assert.Contains("Vegan", chat.Calls[0].UserText);
    }

    [Fact]
    public async Task An_Api_Fault_Soft_Fails_To_An_Empty_List_Without_Throwing()
    {
        var chat = new ScriptedChatClient((_, _) => throw new InvalidOperationException("gateway 500"));
        var suggester = Suggester(chat);

        var result = await suggester.SuggestAsync(Ingredients, Vocabulary, NoAppliedTags);

        Assert.Empty(result);
        Assert.Equal(1, chat.CallCount); // the completion was attempted before it faulted
    }

    [Fact]
    public async Task An_Empty_Response_Soft_Fails_To_An_Empty_List()
    {
        var chat = new ScriptedChatClient((_, _) => ScriptedChatClient.Completion("   ")); // whitespace-only
        var suggester = Suggester(chat);

        var result = await suggester.SuggestAsync(Ingredients, Vocabulary, NoAppliedTags);

        Assert.Empty(result);
        Assert.Equal(1, chat.CallCount);
    }

    [Fact]
    public async Task An_OperationCanceledException_Propagates_Out_Of_SuggestAsync()
    {
        // The adapter's catch filter excludes OCE — cancellation must surface, not soft-fail to an empty list.
        var chat = new ScriptedChatClient((_, _) => throw new OperationCanceledException());
        var suggester = Suggester(chat);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => suggester.SuggestAsync(Ingredients, Vocabulary, NoAppliedTags));
    }

    [Fact]
    public async Task No_Ingredients_Short_Circuits_Before_Any_Completion()
    {
        var chat = new ScriptedChatClient((_, _) => throw new InvalidOperationException("must not be called"));
        var suggester = Suggester(chat);

        var result = await suggester.SuggestAsync([], Vocabulary, NoAppliedTags);

        Assert.Empty(result);
        Assert.Equal(0, chat.CallCount);
    }
}
