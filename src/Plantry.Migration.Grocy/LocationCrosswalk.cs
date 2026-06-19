using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plantry.Migration.Grocy;

/// <summary>
/// The grocy_location_id → plantry_location_id crosswalk written alongside the manifest
/// after the Location commit step. Format: sidecar JSON at &lt;manifest-dir&gt;/location-crosswalk.json.
/// </summary>
public sealed class LocationCrosswalk
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Schema version — currently "1.0".</summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = "1.0";

    /// <summary>UTC timestamp when the crosswalk was written (commit time).</summary>
    [JsonPropertyName("committedAt")]
    public DateTimeOffset CommittedAt { get; init; }

    /// <summary>
    /// Maps Grocy location.id (string key for JSON compatibility) to Plantry location GUID.
    /// </summary>
    [JsonPropertyName("mappings")]
    public Dictionary<string, Guid> Mappings { get; init; } = [];

    // ──────────── Factory / persistence ────────────────────────────────────

    /// <summary>
    /// Resolves the crosswalk file path: same directory as the manifest, named
    /// <c>location-crosswalk.json</c>.
    /// </summary>
    public static string ResolvePath(string manifestFilePath) =>
        Path.Combine(
            Path.GetDirectoryName(manifestFilePath)
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "location-crosswalk.json");

    /// <summary>
    /// Serializes and writes the crosswalk to <paramref name="filePath"/> atomically
    /// (write to temp file, then rename).
    /// </summary>
    public async Task WriteAsync(string filePath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = filePath + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(fs, this, JsonOptions, ct);
        }
        File.Move(tmp, filePath, overwrite: true);
    }

    /// <summary>
    /// Reads an existing crosswalk from <paramref name="filePath"/>.
    /// Returns null if the file does not exist or cannot be parsed.
    /// </summary>
    public static async Task<LocationCrosswalk?> TryReadAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            await using var fs = File.OpenRead(filePath);
            return await JsonSerializer.DeserializeAsync<LocationCrosswalk>(fs, JsonOptions, ct);
        }
        catch
        {
            return null;
        }
    }
}
