using System.Text.Json;
using Microsoft.Extensions.Options;
using Plantry.Catalog.Domain;

namespace Plantry.Migration.Grocy;

/// <summary>
/// Produces an <see cref="ImportSummary"/> by running all staging algorithms (unit, category,
/// location, product, recipe) without writing anything to the Plantry domain databases.
///
/// Used by the /Import/Summary page and the dry-run mode to show counts and tradeoffs
/// before the user commits. Reads from the live catalog to produce accurate match/create
/// counts (using the same repositories as the commit services), but performs zero writes.
/// </summary>
public sealed class ImportSummaryService(
    IOptions<GrocyOptions> options,
    ICategoryRepository categories,
    ILocationRepository locations,
    IProductRepository products)
{
    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Loads the manifest and all available crosswalks, runs staging for all entity types,
    /// and returns the aggregated summary. No writes to Plantry databases.
    /// Returns null when no manifest exists.
    /// </summary>
    public async Task<ImportSummary?> ComputeAsync(CancellationToken ct = default)
    {
        var opts = options.Value;
        var manifestPath = GrocyOptions.ResolveManifestPath(opts);

        if (!File.Exists(manifestPath))
            return null;

        GrocyManifest? manifest;
        try
        {
            await using var fs = File.OpenRead(manifestPath);
            manifest = await JsonSerializer.DeserializeAsync<GrocyManifest>(fs, ReadJsonOptions, ct);
        }
        catch
        {
            return null;
        }

        if (manifest is null)
            return null;

        // ── Load crosswalks (if they exist) ──────────────────────────────────

        var unitCrosswalkPath     = UnitCrosswalk.ResolvePath(manifestPath);
        var categoryCrosswalkPath = CategoryCrosswalk.ResolvePath(manifestPath);
        var locationCrosswalkPath = LocationCrosswalk.ResolvePath(manifestPath);
        var productCrosswalkPath  = ProductCrosswalk.ResolvePath(manifestPath);

        var unitCw     = await UnitCrosswalk.TryReadAsync(unitCrosswalkPath, ct);
        var categoryCw = await CategoryCrosswalk.TryReadAsync(categoryCrosswalkPath, ct);
        var locationCw = await LocationCrosswalk.TryReadAsync(locationCrosswalkPath, ct);
        var productCw  = await ProductCrosswalk.TryReadAsync(productCrosswalkPath, ct);

        // ── Load live catalog names (reads only — no writes) ──────────────────

        var existingCategoryList = await categories.ListActiveAsync(ct);
        var existingCategoryNames = existingCategoryList
            .Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existingLocationList = await locations.ListActiveAsync(ct);
        var existingLocationNames = existingLocationList
            .Select(l => l.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existingProductList = await products.ListActiveAsync(ct);
        var existingProductNames = existingProductList
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ── Unit staging ─────────────────────────────────────────────────────

        var unitRows = UnitStager.Stage(manifest);

        var unitsMatched = unitRows.Count(r =>
            r.Status != UnitStagingStatus.Skipped &&
            r.Action == UnitMappingAction.MatchExisting);

        var unitsCreated = unitRows.Count(r =>
            r.Status != UnitStagingStatus.Skipped &&
            r.Action == UnitMappingAction.CreateNew);

        var unitsSkipped = unitRows.Count(r => r.Status == UnitStagingStatus.Skipped);

        var unitsAnomalyCount = unitRows.Count(r =>
            r.AnomalyNote is not null && r.Status != UnitStagingStatus.Skipped);

        // ── Category staging ──────────────────────────────────────────────────

        var (catMatched, catCreated, catSkipped) = StageCategoryGroups(manifest, existingCategoryNames);

        // ── Location staging ──────────────────────────────────────────────────

        var (locMatched, locCreated, locSkipped) = StageLocations(manifest, existingLocationNames);

        // ── Product staging ───────────────────────────────────────────────────

        var unitMap     = unitCw?.Mappings.ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
        var categoryMap = categoryCw?.Mappings.ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
        var locationMap = locationCw?.Mappings.ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);

        // Pass the live product name set so name-collision flags are set accurately,
        // matching the product review screen (zcw.4). No DB writes.
        var productRows = ProductStager.Stage(
            manifest,
            unitMap,
            categoryMap,
            locationMap,
            existingProductNames: existingProductNames);

        // ── Recipe staging ────────────────────────────────────────────────────

        // Filter out null entries (dropped products) — RecipeStager only needs committed mappings.
        var productMap = productCw?.Mappings
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value!.Value);

        var productIdToName = manifest.Products.ToDictionary(p => p.Id, p => p.Name);
        var unitIdToName    = manifest.QuantityUnits.ToDictionary(u => u.Id, u => u.Name);

        // Dry-run: pass null for existing recipe names — recipe name collisions are
        // not reflected in the summary counts (recipes are not yet in any catalog
        // on first import; collision detection would require the full Recipes list).
        var recipeRows = RecipeStager.Stage(
            manifest,
            productMap,
            productIdToName,
            unitMap,
            unitIdToName,
            existingRecipeNames: null);

        // ── Assemble summary ──────────────────────────────────────────────────

        return new ImportSummary
        {
            // Units
            UnitsMatched      = unitsMatched,
            UnitsCreated      = unitsCreated,
            UnitsSkipped      = unitsSkipped,
            UnitsAnomalyCount = unitsAnomalyCount,

            // Categories
            CategoriesMatched = catMatched,
            CategoriesCreated = catCreated,
            CategoriesSkipped = catSkipped,

            // Locations
            LocationsMatched = locMatched,
            LocationsCreated = locCreated,
            LocationsSkipped = locSkipped,

            // Products
            ProductsTotal    = productRows.Count,
            ProductsVariants = productRows.Count(r => r.IsVariant),
            ProductsFlagged  = productRows.Count(r => r.IsFlagged),

            // Recipes
            RecipesTotal                 = recipeRows.Count,
            RecipesWithFlattenedNestings = recipeRows.Count(r => r.HasFlattenedNesting),
            RecipesWithPhotos            = recipeRows.Count(r => r.PhotoBytes is not null),

            // Metadata
            ManifestExtractedAt = manifest.ExtractedAt,

            // Crosswalk availability
            UnitCrosswalkFound     = unitCw is not null,
            CategoryCrosswalkFound = categoryCw is not null,
            LocationCrosswalkFound = locationCw is not null,
            ProductCrosswalkFound  = productCw is not null,
        };
    }

    // ── Category staging helper ───────────────────────────────────────────────
    // A product group is "matched" when its name matches (case-insensitive, partial)
    // an existing Plantry category name; "created" when it would need a new category.

    /// <summary>
    /// Counts how many Grocy product groups would match existing Plantry categories
    /// (case-insensitive name containment), would create new ones, or are skipped.
    /// </summary>
    /// <param name="manifest">The Grocy manifest.</param>
    /// <param name="existingCategoryNames">Live Plantry category names (case-insensitive set).</param>
    public static (int Matched, int Created, int Skipped) StageCategoryGroups(
        GrocyManifest manifest,
        IReadOnlySet<string> existingCategoryNames)
    {
        int matched = 0, created = 0, skipped = 0;

        foreach (var group in manifest.ProductGroups)
        {
            var name = group.Name?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                skipped++;
                continue;
            }

            if (MatchesExistingName(name, existingCategoryNames))
                matched++;
            else
                created++;
        }

        return (matched, created, skipped);
    }

    // ── Location staging helper ───────────────────────────────────────────────

    /// <summary>
    /// Counts how many Grocy locations would match existing Plantry locations
    /// (case-insensitive name containment), would create new ones, or are skipped.
    /// </summary>
    /// <param name="manifest">The Grocy manifest.</param>
    /// <param name="existingLocationNames">Live Plantry location names (case-insensitive set).</param>
    public static (int Matched, int Created, int Skipped) StageLocations(
        GrocyManifest manifest,
        IReadOnlySet<string> existingLocationNames)
    {
        int matched = 0, created = 0, skipped = 0;

        foreach (var loc in manifest.Locations)
        {
            var name = loc.Name?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                skipped++;
                continue;
            }

            if (MatchesExistingName(name, existingLocationNames))
                matched++;
            else
                created++;
        }

        return (matched, created, skipped);
    }

    // ── Fuzzy-match helper ────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the given Grocy name case-insensitively contains or is contained
    /// by any name in the existing Plantry name set (partial match, mirroring the
    /// CategoryCommitService/LocationCommitService fuzzy algorithm).
    /// </summary>
    public static bool MatchesExistingName(string name, IReadOnlySet<string> existingNames)
    {
        foreach (var existing in existingNames)
        {
            if (existing.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(existing, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
