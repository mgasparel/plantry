using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Plantry.Migration.Grocy;
using Plantry.Recipes.Domain;

namespace Plantry.Web.Pages.Import;

/// <summary>
/// Grocy import pipeline — Recipe staging review screen (plantry-zcw.6).
///
/// GET  — loads the manifest, the product and unit crosswalks, runs
///         <see cref="RecipeStager.Stage"/> to build staging rows, and renders the review list.
/// POST → UpdateName (htmx) — updates a single row's PlantryName to resolve a name collision
///         and returns the refreshed row fragment (OOB swap).
///
/// No commit handler: recipe commit is plantry-zcw.7.
///
/// Access: any authenticated user ([Authorize]).
/// </summary>
[Authorize]
public sealed class RecipesModel(
    IRecipeRepository recipes,
    IOptions<GrocyOptions> options) : PageModel
{
    private const int PageSize = 25;

    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ──────────── State properties ─────────────────────────────────────────

    public bool ManifestExists { get; private set; }
    public string? ManifestPath { get; private set; }

    /// <summary>All staging rows (unfiltered).</summary>
    public IReadOnlyList<RecipeStagingRow> AllRows { get; private set; } = [];

    /// <summary>Current page of staging rows (after pagination).</summary>
    public IReadOnlyList<RecipeStagingRow> PagedRows { get; private set; } = [];

    public int CurrentPage { get; private set; } = 1;
    public int TotalPages { get; private set; } = 1;
    public int TotalRows { get; private set; }

    public string? ErrorMessage { get; private set; }

    // Crosswalk status
    public bool ProductCrosswalkFound { get; private set; }
    public bool UnitCrosswalkFound { get; private set; }

    // ──────────── Summary counts for the page header ───────────────────────

    public int FlaggedCount           => AllRows.Count(r => r.IsFlagged);
    public int NameCollisionCount     => AllRows.Count(r => r.HasNameCollision);
    public int FlattenedNestingCount  => AllRows.Count(r => r.HasFlattenedNesting);
    public int DroppedNotesCount      => AllRows.Count(r => r.HasDroppedNotes);
    public int ProducesProductCount   => AllRows.Count(r => r.HasProducesProduct);
    public int CrosswalkMissingCount  => AllRows.Count(r => r.HasCrosswalkMissing);

    // ──────────── Handlers ─────────────────────────────────────────────────

    public async Task OnGetAsync(int page = 1, CancellationToken ct = default)
    {
        if (!await TryLoadAndStageAsync(ct))
            return;

        CurrentPage = Math.Max(1, page);
        ApplyPaging();
    }

    /// <summary>
    /// htmx partial — updates the PlantryName on a single flagged row and returns the
    /// updated row fragment for an OOB swap. Intended to let the user resolve name
    /// collisions inline without a full page reload.
    ///
    /// POST /Import/Recipes?handler=UpdateName
    /// Body: grocyId (int), newName (string)
    /// </summary>
    public async Task<IActionResult> OnPostUpdateNameAsync(
        int grocyId,
        string newName,
        int page = 1,
        CancellationToken ct = default)
    {
        if (!await TryLoadAndStageAsync(ct))
            return Page();

        var row = AllRows.FirstOrDefault(r => r.GrocyId == grocyId);
        if (row is null)
        {
            ErrorMessage = $"Recipe with Grocy id {grocyId} not found in staging data.";
            return Page();
        }

        var trimmedName = newName.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            ErrorMessage = "Name must not be empty.";
            return Page();
        }

        // Check if the new name collides with an existing recipe
        // We use the household from the current HTTP context via the repository
        // which already scopes to the current household via RLS/query-filter.
        var existingRecipes = await recipes.ListForBrowseAsync(ct);
        var nameExists = existingRecipes.Any(r =>
            string.Equals(r.Name, trimmedName, StringComparison.OrdinalIgnoreCase));

        row.PlantryName = trimmedName;

        if (nameExists)
        {
            // Still collides — keep the flag
            row.Flags |= RecipeStagingFlags.NameCollision;
        }
        else
        {
            // Collision resolved — also check intra-batch collisions
            var otherRow = AllRows.FirstOrDefault(r =>
                r.GrocyId != grocyId &&
                string.Equals(r.PlantryName, trimmedName, StringComparison.OrdinalIgnoreCase));

            if (otherRow is null)
                row.Flags &= ~RecipeStagingFlags.NameCollision;
        }

        CurrentPage = Math.Max(1, page);
        ApplyPaging();
        return Partial("_RecipeStagingRow", row);
    }

    // ──────────── Helpers ──────────────────────────────────────────────────

    private async Task<bool> TryLoadAndStageAsync(CancellationToken ct)
    {
        var opts = options.Value;
        ManifestPath = GrocyOptions.ResolveManifestPath(opts);

        if (!System.IO.File.Exists(ManifestPath))
        {
            ManifestExists = false;
            return false;
        }

        GrocyManifest? manifest;
        try
        {
            await using var fs = System.IO.File.OpenRead(ManifestPath);
            manifest = await JsonSerializer.DeserializeAsync<GrocyManifest>(fs, ReadJsonOptions, ct);
        }
        catch (Exception ex)
        {
            ManifestExists = false;
            ErrorMessage = $"Failed to read manifest: {ex.Message}";
            return false;
        }

        if (manifest is null)
        {
            ManifestExists = false;
            ErrorMessage = "Manifest file could not be parsed. Try re-extracting.";
            return false;
        }

        ManifestExists = true;

        // Load crosswalks
        var productCrosswalkPath = ProductCrosswalk.ResolvePath(ManifestPath);
        var unitCrosswalkPath    = UnitCrosswalk.ResolvePath(ManifestPath);

        var productCw = await ProductCrosswalk.TryReadAsync(productCrosswalkPath, ct);
        var unitCw    = await UnitCrosswalk.TryReadAsync(unitCrosswalkPath, ct);

        ProductCrosswalkFound = productCw is not null;
        UnitCrosswalkFound    = unitCw is not null;

        // Convert crosswalk dictionaries from string-keyed to int-keyed
        var productMap = productCw?.Mappings.ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
        var unitMap    = unitCw?.Mappings.ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);

        // Build product name lookup from manifest
        var productIdToName = manifest.Products
            .ToDictionary(p => p.Id, p => p.Name);

        // Build unit name lookup from manifest
        var unitIdToName = manifest.QuantityUnits
            .ToDictionary(u => u.Id, u => u.Name);

        // Build existing recipe name set for collision detection
        var existingRecipeList = await recipes.ListForBrowseAsync(ct);
        var existingNames = existingRecipeList
            .Select(r => r.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AllRows = RecipeStager.Stage(
            manifest,
            productMap,
            productIdToName,
            unitMap,
            unitIdToName,
            existingNames);

        TotalRows = AllRows.Count;
        return true;
    }

    private void ApplyPaging()
    {
        // Order by GrocyId for a stable, predictable display order
        var ordered = AllRows.OrderBy(r => r.GrocyId).ToList();

        TotalPages  = Math.Max(1, (int)Math.Ceiling(ordered.Count / (double)PageSize));
        CurrentPage = Math.Clamp(CurrentPage, 1, TotalPages);

        PagedRows = ordered
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize)
            .ToList();
    }
}
