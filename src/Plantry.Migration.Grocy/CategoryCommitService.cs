using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Migration.Grocy;

/// <summary>
/// Executes the Category commit step of the Grocy import pipeline (grocy-import-plan.md §7).
///
/// For each staged category (non-Skipped):
///   - MatchExisting → find the Plantry category by name; raise if missing.
///   - CreateNew     → call CreateCategoryCommand; treat DuplicateCategoryName as idempotent.
///
/// Writes the grocy_product_group_id → plantry_category_id crosswalk JSON alongside the manifest.
/// The commit is idempotent: re-running after a partial failure picks up where it left off.
/// </summary>
public sealed class CategoryCommitService(
    ICategoryRepository categories,
    ITenantContext tenant)
{
    /// <summary>Result of a single category commit.</summary>
    public sealed record CategoryCommitResult(
        int GrocyId,
        string GrocyName,
        bool Skipped,
        bool Success,
        Guid? PlantryCategoryId,
        string? ErrorMessage);

    /// <summary>
    /// Commits all non-Skipped staging rows, writes the crosswalk, and returns per-row results.
    /// </summary>
    public async Task<(IReadOnlyList<CategoryCommitResult> Results, string CrosswalkPath)> CommitAsync(
        IReadOnlyList<CategoryStagingRow> stagingRows,
        string manifestFilePath,
        CancellationToken ct = default)
    {
        var results = new List<CategoryCommitResult>(stagingRows.Count);
        var crosswalkMappings = new Dictionary<string, Guid>();

        foreach (var row in stagingRows)
        {
            if (row.Status == CategoryStagingStatus.Skipped)
            {
                results.Add(new CategoryCommitResult(
                    row.GrocyId, row.GrocyName,
                    Skipped: true, Success: true,
                    PlantryCategoryId: null, ErrorMessage: null));
                continue;
            }

            try
            {
                Guid plantryId;

                if (row.Action == CategoryMappingAction.MatchExisting)
                {
                    var existing = await categories.FindByNameAsync(row.PlantryName!, ct);
                    if (existing is null)
                    {
                        results.Add(new CategoryCommitResult(
                            row.GrocyId, row.GrocyName,
                            Skipped: false, Success: false,
                            PlantryCategoryId: null,
                            ErrorMessage: $"Plantry category named '{row.PlantryName}' not found."));
                        continue;
                    }
                    plantryId = existing.Id.Value;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(row.PlantryName))
                    {
                        results.Add(new CategoryCommitResult(
                            row.GrocyId, row.GrocyName,
                            Skipped: false, Success: false,
                            PlantryCategoryId: null,
                            ErrorMessage: "PlantryName is required for CreateNew action."));
                        continue;
                    }

                    var cmd = new CreateCategoryCommand(
                        row.PlantryName, defaultDueDays: null, sortOrder: 0,
                        categories, tenant);

                    var result = await cmd.ExecuteAsync(ct);

                    if (result.IsSuccess)
                    {
                        plantryId = result.Value.Value;
                    }
                    else if (result.Error.Code == "Catalog.DuplicateCategoryName")
                    {
                        var existing = await categories.FindByNameAsync(row.PlantryName!, ct);
                        if (existing is null)
                        {
                            results.Add(new CategoryCommitResult(
                                row.GrocyId, row.GrocyName,
                                Skipped: false, Success: false,
                                PlantryCategoryId: null,
                                ErrorMessage: $"Category '{row.PlantryName}' reported as duplicate but could not be found."));
                            continue;
                        }
                        plantryId = existing.Id.Value;
                    }
                    else
                    {
                        results.Add(new CategoryCommitResult(
                            row.GrocyId, row.GrocyName,
                            Skipped: false, Success: false,
                            PlantryCategoryId: null,
                            ErrorMessage: result.Error.Description));
                        continue;
                    }
                }

                crosswalkMappings[row.GrocyId.ToString()] = plantryId;
                results.Add(new CategoryCommitResult(
                    row.GrocyId, row.GrocyName,
                    Skipped: false, Success: true,
                    PlantryCategoryId: plantryId, ErrorMessage: null));
            }
            catch (Exception ex)
            {
                results.Add(new CategoryCommitResult(
                    row.GrocyId, row.GrocyName,
                    Skipped: false, Success: false,
                    PlantryCategoryId: null,
                    ErrorMessage: $"Unexpected error: {ex.Message}"));
            }
        }

        var crosswalk = new CategoryCrosswalk
        {
            CommittedAt = DateTimeOffset.UtcNow,
            Mappings    = crosswalkMappings,
        };
        var crosswalkPath = CategoryCrosswalk.ResolvePath(manifestFilePath);
        await crosswalk.WriteAsync(crosswalkPath, ct);

        return (results, crosswalkPath);
    }
}
