namespace Plantry.Ai.Infrastructure;

/// <summary>
/// Generic AI provider configuration, bound from the <c>AI</c> configuration section. Shared,
/// context-free config used by every AI adapter (Intake's <c>GeminiReceiptParser</c>, MealPlanning's
/// <c>MealPlannerAiService</c>, Deals' <c>DealMatcher</c>). The OpenAI-compatible <c>ChatClient</c>
/// points at any provider via <see cref="BaseUrl"/> (the PoC uses OpenRouter). The <see cref="ApiKey"/>
/// is sourced from configuration / user-secrets for now; the per-household encrypted key (Slice 8,
/// §7f / DM-7) will replace this single injected value without touching the adapters.
/// <para>
/// Context-specific test/dev seam flags do NOT live here — this type is deliberately generic. The
/// intake fake/sample-parser seams live on <c>IntakeAiOptions</c> (Intake.Infrastructure) and the
/// fake-planner seam on <c>MealPlanningAiOptions</c> (MealPlanning.Infrastructure); all three bind the
/// same <c>AI</c> section (section reuse is a composition concern, not compile-time coupling).
/// </para>
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "AI";

    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "google/gemini-2.5-flash";
}
