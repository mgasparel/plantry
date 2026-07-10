using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Plantry.Migration.Grocy;
using Plantry.Recipes.Domain;

namespace Plantry.Web.Pages.Import;

/// <summary>
/// Grocy import pipeline — Recipe staging review screen (plantry-zcw.6) and commit (plantry-zcw.7).
///
/// GET  — loads the manifest, the product and unit crosswalks, runs
///         <see cref="RecipeStager.Stage"/> to build staging rows, and renders the review list.
/// POST → UpdateName (htmx) — updates a single row's PlantryName to resolve a name collision
///         and returns the refreshed row fragment (OOB swap).
/// POST → CommitRecipes — commits all staged recipes to the Recipes context via
///         <see cref="RecipeCommitService"/>; writes recipe-crosswalk.json and redirects to the
///         same page, which re-stages and shows committed counts.
///
/// Access: any authenticated user ([Authorize]).
/// </summary>
[Authorize]
public sealed class RecipesModel(
    IRecipeRepository recipes,
    RecipeCommitService recipeCommitService,
    RecipeNestingDeflattenService deflattenService,
    IOptions<GrocyOptions> options) : PageModel
{
    private const int PageSize = 25;

    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>The deserialised manifest, retained by <see cref="TryLoadAndStageAsync"/> so the
    /// de-flatten handler can pass its nesting edges without re-reading the file.</summary>
    private GrocyManifest? _manifest;

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
    public bool RecipeCrosswalkFound { get; private set; }

    // ──────────── Drop disposition ─────────────────────────────────────────

    /// <summary>Grocy IDs of recipes the user has explicitly dropped on the review screen.</summary>
    [BindProperty]
    public List<int> DroppedRecipeIds { get; set; } = [];

    /// <summary>
    /// Drop IDs accumulated across pagination pages, carried as repeated <c>droppedIds</c>
    /// query-string parameters. Bound on GET so prior-page drops survive page navigation.
    /// On POST this carries the cross-page drops submitted as hidden form inputs (in addition
    /// to <see cref="DroppedRecipeIds"/> which captures the current page's Alpine-driven inputs).
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public List<int> DroppedIds { get; set; } = [];

    // ──────────── Commit result (populated after POST → CommitRecipes) ───────
    public string? CommitSuccessMessage { get; private set; }
    public IReadOnlyList<RecipeCommitService.RecipeCommitResult> CommitResults { get; private set; } = [];

    // ──────────── De-flatten result (populated after POST → DeflattenNestings) ─
    public string? DeflattenSuccessMessage { get; private set; }
    public RecipeNestingDeflattenService.DeflattenSummary? DeflattenSummary { get; private set; }

    /// <summary>Number of parent recipes carrying nesting edges — the de-flatten candidate set.</summary>
    public int NestingEdgeParentCount =>
        _manifest?.RecipeNestings.Select(n => n.RecipeId).Distinct().Count() ?? 0;

    // ──────────── Drop ID helpers ──────────────────────────────────────────

    /// <summary>
    /// All Grocy recipe IDs that are currently dropped, across all pages.
    /// Used by the view to embed <c>droppedIds</c> parameters in pagination links and in
    /// the commit form (for cross-page rows not rendered on the current page).
    /// </summary>
    public IReadOnlyCollection<int> AllDroppedIds
        => AllRows.Where(r => r.IsDropped).Select(r => r.GrocyId).ToHashSet();

    // ──────────── Summary counts for the page header ───────────────────────

    public int FlaggedCount           => AllRows.Count(r => r.IsFlagged);
    public int NameCollisionCount     => AllRows.Count(r => r.HasNameCollision);
    public int FlattenedNestingCount  => AllRows.Count(r => r.HasFlattenedNesting);
    public int DroppedNotesCount      => AllRows.Count(r => r.HasDroppedNotes);
    public int ProducesProductCount   => AllRows.Count(r => r.HasProducesProduct);
    public int CrosswalkMissingCount  => AllRows.Count(r => r.HasCrosswalkMissing);
    public int DroppedCount           => AllRows.Count(r => r.IsDropped);

    // ──────────── Handlers ─────────────────────────────────────────────────

    public async Task OnGetAsync(int page = 1, CancellationToken ct = default)
    {
        if (!await TryLoadAndStageAsync(ct))
            return;

        // Re-apply any drops accumulated across prior pages before rendering.
        RecipeStager.ApplyDrops(AllRows, MergedDroppedIds());
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

    /// <summary>
    /// Commits all staged recipes to the Recipes context via <see cref="RecipeCommitService"/>.
    ///
    /// POST /Import/Recipes?handler=CommitRecipes
    /// On success: writes recipe-crosswalk.json and re-renders the page with commit counts.
    /// On error: renders the page with an error message.
    /// </summary>
    public async Task<IActionResult> OnPostCommitRecipesAsync(CancellationToken ct = default)
    {
        if (!await TryLoadAndStageAsync(ct))
            return Page();

        if (!ManifestExists || ManifestPath is null)
        {
            ErrorMessage = "Manifest not found — extract Grocy data first.";
            return Page();
        }

        // Mark dropped rows — merge current-page Alpine inputs with cross-page hidden inputs.
        RecipeStager.ApplyDrops(AllRows, MergedDroppedIds());

        var (results, _) = await recipeCommitService.CommitAsync(AllRows, ManifestPath, ct);
        CommitResults = results;

        var committed = results.Count(r => r.Success && !r.Skipped);
        var failed    = results.Count(r => !r.Success && !r.Skipped);
        var skipped   = results.Count(r => r.Skipped);

        if (failed > 0)
        {
            ErrorMessage = $"Commit completed with {failed} failure(s). " +
                           $"{committed} recipe(s) committed, {skipped} skipped. " +
                           "See details below.";
        }
        else
        {
            CommitSuccessMessage = $"{committed} recipe(s) committed successfully. " +
                                   $"{skipped} skipped (no committable ingredients).";
        }

        // Re-load crosswalk status for the UI
        var recipeCrosswalkPath = RecipeCrosswalk.ResolvePath(ManifestPath);
        RecipeCrosswalkFound = System.IO.File.Exists(recipeCrosswalkPath);

        CurrentPage = 1;
        ApplyPaging();
        return Page();
    }

    /// <summary>
    /// Re-imports the Grocy nesting edges as recipe inclusions via
    /// <see cref="RecipeNestingDeflattenService"/> (recipe-composition.md §10 / D16). Only untouched-since-import
    /// parents are converted; edited parents and those with an uncommitted sub are skipped and reported.
    ///
    /// POST /Import/Recipes?handler=DeflattenNestings
    /// Run after recipes have been committed (the crosswalk maps Grocy ids → committed recipe ids).
    /// </summary>
    public async Task<IActionResult> OnPostDeflattenNestingsAsync(CancellationToken ct = default)
    {
        if (!await TryLoadAndStageAsync(ct))
            return Page();

        if (!ManifestExists || ManifestPath is null || _manifest is null)
        {
            ErrorMessage = "Manifest not found — extract Grocy data first.";
            return Page();
        }

        if (!RecipeCrosswalkFound)
        {
            ErrorMessage = "Recipes have not been committed yet — commit recipes before converting nestings.";
            CurrentPage = 1;
            ApplyPaging();
            return Page();
        }

        var summary = await deflattenService.DeflattenAsync(AllRows, _manifest, ManifestPath, ct);
        DeflattenSummary = summary;

        if (summary.ParentsWithNestings == 0)
        {
            DeflattenSuccessMessage = "No nesting edges found — nothing to convert.";
        }
        else if (summary.Failed > 0)
        {
            ErrorMessage = $"Nesting conversion completed with {summary.Failed} failure(s). " +
                           $"{summary.Converted} converted, {summary.Skipped} skipped. See details below.";
        }
        else
        {
            DeflattenSuccessMessage =
                $"{summary.Converted} recipe(s) converted to inclusions. " +
                $"{summary.AlreadyConverted} already converted, " +
                $"{summary.SkippedEdited} skipped (edited since import), " +
                $"{summary.SkippedMissingSub} skipped (uncommitted sub).";
        }

        CurrentPage = 1;
        ApplyPaging();
        return Page();
    }

    // ──────────── Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Returns the unified set of dropped recipe Grocy IDs by merging
    /// <see cref="DroppedRecipeIds"/> (current-page Alpine-driven hidden inputs on POST,
    /// or empty on GET) with <see cref="DroppedIds"/> (accumulated cross-page drops carried
    /// as repeated <c>droppedIds</c> query-string parameters on GET, or as hidden form inputs
    /// on POST). Safe to call on GET and POST alike.
    ///
    /// The actual row-stamping is delegated to <see cref="RecipeStager.ApplyDrops"/> so
    /// it can be tested independently without the full web stack.
    /// </summary>
    private IEnumerable<int> MergedDroppedIds()
    {
        var merged = new HashSet<int>(DroppedRecipeIds);
        merged.UnionWith(DroppedIds);
        return merged;
    }

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
        _manifest = manifest;

        // Load crosswalks
        var productCrosswalkPath = ProductCrosswalk.ResolvePath(ManifestPath);
        var unitCrosswalkPath    = UnitCrosswalk.ResolvePath(ManifestPath);
        var recipeCrosswalkPath  = RecipeCrosswalk.ResolvePath(ManifestPath);

        var productCw = await ProductCrosswalk.TryReadAsync(productCrosswalkPath, ct);
        var unitCw    = await UnitCrosswalk.TryReadAsync(unitCrosswalkPath, ct);

        ProductCrosswalkFound = productCw is not null;
        UnitCrosswalkFound    = unitCw is not null;
        RecipeCrosswalkFound  = System.IO.File.Exists(recipeCrosswalkPath);

        // Convert crosswalk dictionaries from string-keyed to int-keyed.
        // Filter out null entries (dropped products/units) — RecipeStager only uses committed mappings.
        var productMap = productCw?.Mappings
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value!.Value);
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
