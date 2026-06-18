using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Plantry.Migration.Grocy;
using Plantry.Web.Pages.Shared;

namespace Plantry.Web.Pages.Import;

/// <summary>
/// Grocy import entry point (plantry-zcw.1).
/// GET  — shows the current manifest state (file path + collection counts) if a manifest exists.
/// POST → Extract — invokes <see cref="ExtractCommand"/>, writes the manifest, then renders the updated state.
/// Access: any authenticated user (household-owner check is sufficient for dogfood — [Authorize]).
/// </summary>
[Authorize]
public sealed class IndexModel(
    ExtractCommand extract,
    IOptions<GrocyOptions> options) : PageModel
{
    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public bool GrocyConfigured { get; private set; }
    public bool ManifestExists { get; private set; }
    public string? ManifestPath { get; private set; }
    public DateTimeOffset? ManifestExtractedAt { get; private set; }

    /// <summary>Manifest collection counts displayed in the DataGrid, populated when a manifest exists.</summary>
    public DataGridViewModel? CollectionGrid { get; private set; }

    public string? ErrorMessage { get; private set; }

    public void OnGet()
    {
        LoadState();
    }

    public async Task<IActionResult> OnPostExtractAsync(CancellationToken ct)
    {
        LoadState();

        if (!GrocyConfigured)
        {
            ErrorMessage = "Grocy is not configured. Set Grocy:Url and Grocy:ApiKey in user secrets.";
            return Page();
        }

        try
        {
            var (manifest, filePath) = await extract.ExecuteAsync(ct);
            ManifestPath = filePath;
            ManifestExists = true;
            ManifestExtractedAt = manifest.ExtractedAt;
            CollectionGrid = BuildGrid(ManifestCounts.From(manifest));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Extraction failed: {ex.Message}";
        }

        return Page();
    }

    private void LoadState()
    {
        var opts = options.Value;
        GrocyConfigured = !string.IsNullOrWhiteSpace(opts.Url)
                       && !string.IsNullOrWhiteSpace(opts.ApiKey);

        ManifestPath = GrocyOptions.ResolveManifestPath(opts);

        if (!System.IO.File.Exists(ManifestPath))
            return;

        try
        {
            using var fs = System.IO.File.OpenRead(ManifestPath);
            var manifest = JsonSerializer.Deserialize<GrocyManifest>(fs, ReadJsonOptions);
            if (manifest is not null)
            {
                ManifestExists = true;
                ManifestExtractedAt = manifest.ExtractedAt;
                CollectionGrid = BuildGrid(ManifestCounts.From(manifest));
            }
        }
        catch
        {
            // Corrupted manifest — surface nothing; let the user re-extract.
        }
    }

    /// <summary>Builds the collection-counts DataGrid from the manifest counts.</summary>
    private static DataGridViewModel BuildGrid(ManifestCounts c)
    {
        static GridRow Row(string collection, int count, string notes) =>
            new([GridCell.Text(collection), GridCell.Text(count.ToString()), GridCell.Muted(notes)]);

        return new DataGridViewModel(
            Columns:
            [
                new GridColumn("Collection"),
                new GridColumn("Rows", GridAlign.End),
                new GridColumn("Notes"),
            ],
            Rows:
            [
                Row("Products",         c.Products,               "All products"),
                Row("Quantity units",   c.QuantityUnits,          ""),
                Row("Unit conversions", c.QuantityUnitConversions, "Global + product-specific"),
                Row("Locations",        c.Locations,              ""),
                Row("Product groups",   c.ProductGroups,          "→ categories"),
                Row("Recipes",          c.Recipes,                "Normal recipes only (meal-plan artifacts excluded)"),
                Row("Recipe ingredients", c.RecipePositions,      "Belonging to normal recipes"),
                Row("Recipe nestings",  c.RecipeNestings,         "Sub-recipe inclusions on normal recipes"),
                Row("Userfields",       c.Userfields,             "Incl. recipes.original_recipe (source URL)"),
                Row("Product barcodes", c.ProductBarcodes,        "Parked (no catalog barcode field)"),
            ],
            EmptyMessage: "No collections found.",
            Id: "grocy-manifest-grid");
    }
}
