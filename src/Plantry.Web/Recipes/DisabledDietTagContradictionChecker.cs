using Plantry.Recipes.Application;

namespace Plantry.Web.Recipes;

/// <summary>
/// No-op <see cref="IDietTagContradictionChecker"/> registered when no AI API key is configured. The real
/// <c>DietTagContradictionChecker</c> builds an OpenAI <c>ChatClient</c> at construction (which requires a
/// non-empty key), so a keyless host — dev without secrets, or the E2E stack — would fail DI resolution once the
/// recipe editor's post-save nudge runs. This stand-in lets the host start and degrades to no nudge (empty list),
/// exactly as an AI soft-fail would (mirrors <c>DisabledRecipeTagSuggester</c> / <c>DisabledDealMatcher</c>).
/// The save is entirely unaffected.
/// </summary>
public sealed class DisabledDietTagContradictionChecker : IDietTagContradictionChecker
{
    public Task<IReadOnlyList<DietTagContradiction>> CheckAsync(
        IReadOnlyList<string> ingredientNames,
        IReadOnlyList<string> dietTagNames,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DietTagContradiction>>([]);
}
