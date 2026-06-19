using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Plantry.Catalog.Domain;
using Plantry.Migration.Grocy;

namespace Plantry.Web.Pages.Import;

/// <summary>
/// Grocy import pipeline — Category mapping grid (plantry-zcw.9).
///
/// GET  — loads the manifest, queries existing category names, runs <see cref="CategoryStager.Stage"/>
///         to build staging rows, and renders the mapping grid for user review.
/// POST → Commit — re-accepts the (potentially edited) staging rows from the form,
///         calls <see cref="CategoryCommitService.CommitAsync"/>, and writes the crosswalk.
///
/// Access: any authenticated user ([Authorize]).
/// </summary>
[Authorize]
public sealed class CategoriesModel(
    CategoryCommitService commitService,
    ICategoryRepository categoryRepository,
    IOptions<GrocyOptions> options) : PageModel
{
    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ──────────── State properties ─────────────────────────────────────────

    public bool ManifestExists { get; private set; }
    public string? ManifestPath { get; private set; }

    /// <summary>Staging rows bound on GET (from algorithm) and on POST (from form submission).</summary>
    [BindProperty]
    public List<CategoryStagingRow> StagingRows { get; set; } = [];

    public string? ErrorMessage { get; private set; }

    // Commit result state
    public bool CommitSuccess { get; private set; }
    public string? CommitSummary { get; private set; }
    public string? CrosswalkPath { get; private set; }

    // ──────────── Summary counts for the page header ───────────────────────

    public int AutoCount        => StagingRows.Count(r => r.Status == CategoryStagingStatus.Auto);
    public int NeedsReviewCount => StagingRows.Count(r => r.Status == CategoryStagingStatus.NeedsReview);
    public int SkippedCount     => StagingRows.Count(r => r.Status == CategoryStagingStatus.Skipped);
    public int AnomalyCount     => StagingRows.Count(r => r.AnomalyNote is not null && r.Status != CategoryStagingStatus.Skipped);

    // ──────────── Handlers ─────────────────────────────────────────────────

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (!TryLoadManifest(out var manifest))
            return;

        var existingNames = (await categoryRepository.ListActiveAsync(ct))
            .Select(c => c.Name)
            .ToList();

        StagingRows = CategoryStager.Stage(manifest!, existingNames).ToList();
    }

    public async Task<IActionResult> OnPostCommitAsync(CancellationToken ct)
    {
        if (!TryLoadManifest(out _))
            return Page();

        if (StagingRows.Count == 0)
        {
            ErrorMessage = "No staging rows were submitted.";
            return Page();
        }

        try
        {
            var (results, crosswalkPath) = await commitService.CommitAsync(
                StagingRows, ManifestPath!, ct);

            CrosswalkPath = crosswalkPath;

            var succeeded = results.Count(r => r.Success && !r.Skipped);
            var skipped   = results.Count(r => r.Skipped);
            var failed    = results.Count(r => !r.Success);

            if (failed > 0)
            {
                var errors = results
                    .Where(r => !r.Success)
                    .Select(r => $"{r.GrocyName}: {r.ErrorMessage}")
                    .ToList();
                ErrorMessage = $"{failed} category(ies) failed to commit: {string.Join("; ", errors)}";

                // Partially succeeded — surface what we can
                if (succeeded > 0)
                {
                    CommitSuccess = true;
                    CommitSummary = $"{succeeded} committed, {skipped} skipped, {failed} failed.";
                }
            }
            else
            {
                CommitSuccess = true;
                CommitSummary = $"{succeeded} committed, {skipped} skipped.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Commit failed: {ex.Message}";
        }

        return Page();
    }

    // ──────────── Helpers ──────────────────────────────────────────────────

    private bool TryLoadManifest(out GrocyManifest? manifest)
    {
        manifest = null;
        var opts = options.Value;
        ManifestPath = GrocyOptions.ResolveManifestPath(opts);

        if (!System.IO.File.Exists(ManifestPath))
        {
            ManifestExists = false;
            return false;
        }

        try
        {
            using var fs = System.IO.File.OpenRead(ManifestPath);
            manifest = JsonSerializer.Deserialize<GrocyManifest>(fs, ReadJsonOptions);
            if (manifest is null)
            {
                ManifestExists = false;
                ErrorMessage = "Manifest file could not be read. Try re-extracting.";
                return false;
            }

            ManifestExists = true;
            return true;
        }
        catch (Exception ex)
        {
            ManifestExists = false;
            ErrorMessage = $"Failed to read manifest: {ex.Message}";
            return false;
        }
    }
}
