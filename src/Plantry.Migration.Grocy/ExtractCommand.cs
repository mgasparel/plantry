using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Plantry.Migration.Grocy;

/// <summary>
/// Orchestrates all GrocyClient calls and writes the resulting <see cref="GrocyManifest"/>
/// to the configured manifest file path. Re-runnable — always overwrites.
/// This is the Extract stage of the grocy-import-plan.md §2.1 pipeline.
/// </summary>
public sealed class ExtractCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly GrocyClient _client;
    private readonly GrocyOptions _options;

    public ExtractCommand(GrocyClient client, IOptions<GrocyOptions> options)
    {
        _client = client;
        _options = options.Value;
    }

    /// <summary>
    /// Fetches all in-scope Grocy collections and writes the manifest JSON.
    /// Recipes are filtered client-side to <c>type == "normal"</c> (§3 — 65 of 415);
    /// recipe positions and nestings are filtered to only those belonging to normal recipes.
    /// </summary>
    /// <returns>The written manifest and the resolved file path.</returns>
    public async Task<(GrocyManifest Manifest, string FilePath)> ExecuteAsync(CancellationToken ct = default)
    {
        // Fetch all collections in parallel — Grocy is a single-user SQLite instance,
        // so concurrent reads are safe and this keeps the extract fast.
        var productsTask = _client.GetProductsAsync(ct);
        var unitsTask = _client.GetQuantityUnitsAsync(ct);
        var conversionsTask = _client.GetQuantityUnitConversionsAsync(ct);
        var locationsTask = _client.GetLocationsAsync(ct);
        var groupsTask = _client.GetProductGroupsAsync(ct);
        var recipesTask = _client.GetRecipesAsync(ct);
        var positionsTask = _client.GetRecipePositionsAsync(ct);
        var nestingsTask = _client.GetRecipeNestingsAsync(ct);
        var userfieldsTask = _client.GetUserfieldsAsync(ct);
        var barcodesTask = _client.GetProductBarcodesAsync(ct);

        await Task.WhenAll(
            productsTask, unitsTask, conversionsTask, locationsTask, groupsTask,
            recipesTask, positionsTask, nestingsTask, userfieldsTask, barcodesTask);

        var allRecipes = await recipesTask;

        // Filter client-side: only normal recipes (§3 — meal-plan artifacts are out of scope).
        var normalRecipes = allRecipes
            .Where(r => string.Equals(r.Type, "normal", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var normalRecipeIds = normalRecipes.Select(r => r.Id).ToHashSet();

        // Filter positions and nestings to those belonging to normal recipes.
        var allPositions = await positionsTask;
        var normalPositions = allPositions
            .Where(p => normalRecipeIds.Contains(p.RecipeId))
            .ToList();

        var allNestings = await nestingsTask;
        var normalNestings = allNestings
            .Where(n => normalRecipeIds.Contains(n.RecipeId))
            .ToList();

        var manifest = new GrocyManifest
        {
            ExtractedAt = DateTimeOffset.UtcNow,
            Products = await productsTask,
            QuantityUnits = await unitsTask,
            QuantityUnitConversions = await conversionsTask,
            Locations = await locationsTask,
            ProductGroups = await groupsTask,
            Recipes = normalRecipes,
            RecipePositions = normalPositions,
            RecipeNestings = normalNestings,
            Userfields = await userfieldsTask,
            ProductBarcodes = await barcodesTask,
        };

        var filePath = ResolveManifestPath();
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(fs, manifest, JsonOptions, ct);

        return (manifest, filePath);
    }

    private string ResolveManifestPath() => GrocyOptions.ResolveManifestPath(_options);
}
