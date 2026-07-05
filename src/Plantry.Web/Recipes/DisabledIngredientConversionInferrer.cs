using Plantry.Recipes.Application;

namespace Plantry.Web.Recipes;

/// <summary>
/// No-op <see cref="IIngredientConversionInferrer"/> registered when no AI API key is configured
/// (plantry-qll2.4). The real <c>IngredientConversionInferrer</c> builds an OpenAI <c>ChatClient</c> at
/// construction (which requires a non-empty key), so a keyless host — dev without secrets, or the E2E
/// stack — would fail DI resolution. This stand-in lets the host start and reports
/// <see cref="IsAvailable"/> = <c>false</c>, so the recipe editor does not defer a missing conversion to
/// a seeder that cannot run and instead falls back to the manual C10 conversion prompt (today's
/// behaviour). <see cref="InferFactorAsync"/> returns <c>null</c> if ever called, exactly as an AI
/// soft-fail would (mirrors <c>DisabledDietTagContradictionChecker</c> / <c>DisabledRecipeTagSuggester</c>).
/// </summary>
public sealed class DisabledIngredientConversionInferrer : IIngredientConversionInferrer
{
    public bool IsAvailable => false;

    public Task<decimal?> InferFactorAsync(
        string productName, string fromUnitCode, string toUnitCode, CancellationToken ct = default)
        => Task.FromResult<decimal?>(null);
}
