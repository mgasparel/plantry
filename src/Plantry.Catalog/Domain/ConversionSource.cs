namespace Plantry.Catalog.Domain;

/// <summary>
/// Provenance of a <see cref="ProductConversion"/>'s factor (ADR-022). A conversion is either
/// authored/endorsed by the user (<see cref="UserConfirmed"/>) or machine-guessed and not yet
/// endorsed (<see cref="AiSuggested"/>). Provenance is <b>metadata, not a computation gate</b> —
/// an AI-suggested factor flows through <see cref="UnitConverter"/> exactly like a confirmed one;
/// it is merely surfaced to the user as unconfirmed with a one-click path to promote it.
/// </summary>
public enum ConversionSource
{
    /// <summary>User-authored or user-endorsed — the historical, implicitly-authoritative source.</summary>
    UserConfirmed,

    /// <summary>Machine-guessed (e.g. an intake LLM learned "1 lb bananas ≈ 5 each"); usable but unconfirmed.</summary>
    AiSuggested,
}

public static class ConversionSourceExtensions
{
    public static ConversionSource Parse(string value) => value switch
    {
        "user_confirmed" => ConversionSource.UserConfirmed,
        "ai_suggested" => ConversionSource.AiSuggested,
        _ => throw new ArgumentException($"Unknown conversion source '{value}'.", nameof(value)),
    };

    public static string ToDbValue(this ConversionSource source) => source switch
    {
        ConversionSource.UserConfirmed => "user_confirmed",
        ConversionSource.AiSuggested => "ai_suggested",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };
}
