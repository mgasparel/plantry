using Plantry.Recipes.Application;

namespace Plantry.Web.Recipes;

/// <summary>
/// No-op <see cref="IRecipeTagSuggester"/> registered when no AI API key is configured. The real
/// <c>RecipeTagSuggester</c> builds an OpenAI <c>ChatClient</c> at construction (which requires a
/// non-empty key), so a keyless host — dev without secrets, or the E2E stack — would fail DI resolution
/// once the recipe editor requests suggestions. This stand-in lets the host start and degrades to no
/// suggestions (empty list), exactly as an AI soft-fail would (mirrors <c>DisabledDealMatcher</c>).
/// The editor renders no suggestion chips, and manual tagging is entirely unaffected.
/// </summary>
public sealed class DisabledRecipeTagSuggester : IRecipeTagSuggester
{
    public Task<IReadOnlyList<TagSuggestion>> SuggestAsync(
        IReadOnlyList<string> ingredientNames,
        IReadOnlyList<TagVocabularyEntry> vocabulary,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TagSuggestion>>([]);
}
