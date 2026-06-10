namespace Plantry.Intake.Infrastructure;

/// <summary>
/// AI provider configuration for the receipt parser, bound from the <c>AI</c> configuration section.
/// The OpenAI-compatible <c>ChatClient</c> points at any provider via <see cref="BaseUrl"/> (the PoC
/// uses OpenRouter). The <see cref="ApiKey"/> is sourced from configuration / user-secrets for now;
/// the per-household encrypted key (Slice 8, §7f / DM-7) will replace this single injected value
/// without touching the adapter.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "AI";

    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "google/gemini-2.5-flash";
}
