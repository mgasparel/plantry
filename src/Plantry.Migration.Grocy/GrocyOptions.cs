namespace Plantry.Migration.Grocy;

/// <summary>
/// Configuration for the Grocy import pipeline.
/// Bound from the "Grocy" section in appsettings / user secrets / environment variables.
/// </summary>
public sealed class GrocyOptions
{
    public const string SectionName = "Grocy";

    /// <summary>Base URL of the Grocy instance, e.g. https://grocy.example.com</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>Grocy API key (user secrets in dev, env var in prod Docker).</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// File path where the manifest JSON snapshot will be written.
    /// Defaults to <c>%LocalAppData%/Plantry/grocy-manifest.json</c> when not configured.
    /// </summary>
    public string ManifestPath { get; init; } = string.Empty;

    /// <summary>
    /// Resolves the effective manifest path: uses the configured value if set, otherwise falls back
    /// to a platform-local default. Centralised here so both <see cref="ExtractCommand"/> and the
    /// web page model share the same default without duplication.
    /// </summary>
    public static string ResolveManifestPath(GrocyOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.ManifestPath))
            return opts.ManifestPath;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Plantry",
            "grocy-manifest.json");
    }
}
