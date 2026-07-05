namespace Plantry.Intake.Infrastructure;

/// <summary>
/// Intake-owned AI seam flags, bound from the same <c>AI</c> configuration section as the generic
/// <c>Plantry.Ai.Infrastructure.AiOptions</c> (section reuse is a composition concern, not compile-time
/// coupling). These are deterministic, no-network test/dev seams for the receipt parser — they belong to
/// the Intake context, not the shared AI library, so the shared library stays generic.
/// </summary>
public sealed class IntakeAiOptions
{
    public const string SectionName = "AI";

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
