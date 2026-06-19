using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Plantry.Catalog.Domain;
using Plantry.Migration.Grocy;

namespace Plantry.Web.Pages.Import;

/// <summary>
/// Grocy import pipeline — Product staging review screen (plantry-zcw.4).
///
/// GET  — loads the manifest and the three crosswalk sidecars, runs
///         <see cref="ProductStager.Stage"/> to build staging rows, and renders the review list.
/// POST → UpdateName (htmx) — updates a single row's PlantryName to resolve a name collision
///         and returns the refreshed row fragment (OOB swap).
///
/// Access: any authenticated user ([Authorize]).
/// No domain writes occur here — that is zcw.5 (Product commit).
/// </summary>
[Authorize]
public sealed class ProductsModel(
    IProductRepository products,
    IOptions<GrocyOptions> options) : PageModel
{
    private const int PageSize = 50;

    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ──────────── State properties ─────────────────────────────────────────

    public bool ManifestExists { get; private set; }
    public string? ManifestPath { get; private set; }

    /// <summary>All staging rows (unfiltered).</summary>
    public IReadOnlyList<ProductStagingRow> AllRows { get; private set; } = [];

    /// <summary>Current page of staging rows (after parent/variant grouping + pagination).</summary>
    public IReadOnlyList<ProductStagingRow> PagedRows { get; private set; } = [];

    public int CurrentPage { get; private set; } = 1;
    public int TotalPages { get; private set; } = 1;
    public int TotalRows { get; private set; }

    public string? ErrorMessage { get; private set; }

    // Crosswalk status
    public bool UnitCrosswalkFound { get; private set; }
    public bool CategoryCrosswalkFound { get; private set; }
    public bool LocationCrosswalkFound { get; private set; }

    // ──────────── Summary counts for the page header ───────────────────────

    public int FlaggedCount          => AllRows.Count(r => r.IsFlagged);
    public int NameCollisionCount    => AllRows.Count(r => r.HasNameCollision);
    public int VariantCount          => AllRows.Count(r => r.IsVariant);
    public int DroppedBarcodeCount   => AllRows.Count(r => r.HasDroppedBarcode);
    public int MultiUnitCount        => AllRows.Count(r => r.IsMultiUnit);
    public int CrosswalkMissingCount => AllRows.Count(r => r.HasCrosswalkMissing);

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
    /// POST /Import/Products?handler=UpdateName
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
            ErrorMessage = $"Product with Grocy id {grocyId} not found in staging data.";
            return Page();
        }

        // Apply the new name and re-check for collisions against the current catalog
        var trimmedName = newName.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            ErrorMessage = "Name must not be empty.";
            return Page();
        }

        // Check if the new name collides with an existing catalog product
        var existingByName = await products.FindByNameAsync(trimmedName, ct);
        if (existingByName is not null)
        {
            // Still a collision — keep the flag, update name so user sees what they typed
            row.PlantryName = trimmedName;
        }
        else
        {
            // Collision resolved
            row.PlantryName = trimmedName;
            row.Flags &= ~ProductStagingFlags.NameCollision;
        }

        // Return htmx partial with just this row
        CurrentPage = Math.Max(1, page);
        ApplyPaging();
        return Partial("_ProductStagingRow", row);
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
        var unitCrosswalkPath    = UnitCrosswalk.ResolvePath(ManifestPath);
        var categoryCrosswalkPath = CategoryCrosswalk.ResolvePath(ManifestPath);
        var locationCrosswalkPath = LocationCrosswalk.ResolvePath(ManifestPath);

        var unitCw     = await UnitCrosswalk.TryReadAsync(unitCrosswalkPath, ct);
        var categoryCw = await CategoryCrosswalk.TryReadAsync(categoryCrosswalkPath, ct);
        var locationCw = await LocationCrosswalk.TryReadAsync(locationCrosswalkPath, ct);

        UnitCrosswalkFound     = unitCw is not null;
        CategoryCrosswalkFound = categoryCw is not null;
        LocationCrosswalkFound = locationCw is not null;

        // Convert crosswalk dictionaries from string-keyed to int-keyed
        var unitMap     = unitCw?.Mappings.ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
        var categoryMap = categoryCw?.Mappings.ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
        var locationMap = locationCw?.Mappings.ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);

        // Build existing product name set for collision detection
        var existingProducts = await products.ListActiveAsync(ct);
        var existingNames = existingProducts
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AllRows = ProductStager.Stage(
            manifest,
            unitMap,
            categoryMap,
            locationMap,
            existingNames);

        TotalRows = AllRows.Count;
        return true;
    }

    private void ApplyPaging()
    {
        // Group: parents first (non-variants), then their variants immediately after.
        // Within each group, order by GrocyId.
        var parentRows  = AllRows.Where(r => !r.IsVariant).OrderBy(r => r.GrocyId).ToList();
        var variantRows = AllRows.Where(r => r.IsVariant).OrderBy(r => r.GrocyId).ToList();

        // Interleave variants under their parents
        var ordered = new List<ProductStagingRow>();
        foreach (var parent in parentRows)
        {
            ordered.Add(parent);
            var children = variantRows.Where(v => v.GrocyParentProductId == parent.GrocyId);
            ordered.AddRange(children);
        }

        // Add any orphan variants (parent not in manifest — shouldn't happen but guard against it)
        var inlined = ordered.Select(r => r.GrocyId).ToHashSet();
        ordered.AddRange(variantRows.Where(v => !inlined.Contains(v.GrocyId)));

        TotalPages  = Math.Max(1, (int)Math.Ceiling(ordered.Count / (double)PageSize));
        CurrentPage = Math.Clamp(CurrentPage, 1, TotalPages);

        PagedRows = ordered
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize)
            .ToList();
    }
}
