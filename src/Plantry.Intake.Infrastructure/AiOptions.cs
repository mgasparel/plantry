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

    /// <summary>
    /// Test-only seam: when true, the Web host registers a deterministic, no-network fake
    /// <c>IReceiptParser</c> instead of the real Gemini parser, so the end-to-end intake journey runs
    /// without any AI call or API key. Defaults to false — production always uses the real parser. Set
    /// only by the E2E AppHost (plantry-zbk); never enable it outside a test run.
    /// </summary>
    public bool UseFakeParser { get; set; } = false;

    /// <summary>
    /// Dev-only seam: when true, the Web host registers <c>SampleReceiptParser</c> — a deterministic,
    /// no-network parser seeded from a real scanned receipt — so the intake UI can be iterated locally
    /// without spending AI credits. Defaults to false; intended only for <c>appsettings.Development.json</c>.
    /// Takes precedence over <see cref="UseFakeParser"/> and the real Gemini parser when set.
    /// </summary>
    public bool UseSampleParser { get; set; } = false;
}
