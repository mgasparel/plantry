using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Migration.Grocy;

/// <summary>
/// Executes the Unit commit step of the Grocy import pipeline (grocy-import-plan.md §7).
///
/// For each staged unit (non-Skipped):
///   - MatchExisting → find the Plantry unit by code (via IUnitRepository); raise if missing.
///   - CreateNew     → call CreateUnitCommand; treat DuplicateUnitCode as idempotent (already exists).
///
/// Writes the grocy_unit_id → plantry_unit_id crosswalk JSON alongside the manifest.
/// The commit is idempotent: re-running after a partial failure picks up where it left off
/// because duplicate codes return the existing unit's id.
/// </summary>
public sealed class UnitCommitService(
    IUnitRepository units,
    ITenantContext tenant)
{
    /// <summary>
    /// Result of a single unit commit.
    /// </summary>
    public sealed record UnitCommitResult(
        int GrocyId,
        string GrocyName,
        bool Skipped,
        bool Success,
        Guid? PlantryUnitId,
        string? ErrorMessage);

    /// <summary>
    /// Commits all non-Skipped staging rows, writes the crosswalk, and returns per-row results.
    /// </summary>
    /// <param name="stagingRows">The staging rows (as confirmed by the user on the mapping grid).</param>
    /// <param name="manifestFilePath">
    /// The manifest file path — used to derive the crosswalk sidecar path.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Per-unit commit results and the path to the written crosswalk file.
    /// </returns>
    public async Task<(IReadOnlyList<UnitCommitResult> Results, string CrosswalkPath)> CommitAsync(
        IReadOnlyList<UnitStagingRow> stagingRows,
        string manifestFilePath,
        CancellationToken ct = default)
    {
        var results = new List<UnitCommitResult>(stagingRows.Count);
        var crosswalkMappings = new Dictionary<string, Guid>();

        foreach (var row in stagingRows)
        {
            if (row.Status == UnitStagingStatus.Skipped)
            {
                results.Add(new UnitCommitResult(
                    row.GrocyId, row.GrocyName,
                    Skipped: true, Success: true,
                    PlantryUnitId: null, ErrorMessage: null));
                continue;
            }

            try
            {
                Guid plantryId;

                if (row.Action == UnitMappingAction.MatchExisting)
                {
                    // Find by code — code is authoritative for seed-matched units
                    var existing = await units.FindByCodeAsync(row.PlantryCode!, ct);
                    if (existing is null)
                    {
                        results.Add(new UnitCommitResult(
                            row.GrocyId, row.GrocyName,
                            Skipped: false, Success: false,
                            PlantryUnitId: null,
                            ErrorMessage: $"Plantry unit with code '{row.PlantryCode}' not found. " +
                                          "Ensure the catalog reference data seeder has run."));
                        continue;
                    }
                    plantryId = existing.Id.Value;
                }
                else
                {
                    // CreateNew — use CreateUnitCommand; treat duplicate code as idempotent
                    if (string.IsNullOrWhiteSpace(row.PlantryCode) || string.IsNullOrWhiteSpace(row.PlantryName))
                    {
                        results.Add(new UnitCommitResult(
                            row.GrocyId, row.GrocyName,
                            Skipped: false, Success: false,
                            PlantryUnitId: null,
                            ErrorMessage: "PlantryCode and PlantryName are required for CreateNew action."));
                        continue;
                    }

                    var dimension = Catalog.Domain.DimensionExtensions.Parse(row.Dimension);
                    var cmd = new CreateUnitCommand(
                        row.PlantryCode, row.PlantryName,
                        dimension, row.FactorToBase,
                        isBase: false,
                        units, tenant);

                    var result = await cmd.ExecuteAsync(ct);

                    if (result.IsSuccess)
                    {
                        plantryId = result.Value.Value;
                    }
                    else if (result.Error.Code == "Catalog.DuplicateUnitCode")
                    {
                        // Idempotent: already created — find existing
                        var existing = await units.FindByCodeAsync(row.PlantryCode!, ct);
                        if (existing is null)
                        {
                            results.Add(new UnitCommitResult(
                                row.GrocyId, row.GrocyName,
                                Skipped: false, Success: false,
                                PlantryUnitId: null,
                                ErrorMessage: $"Unit code '{row.PlantryCode}' reported as duplicate but could not be found."));
                            continue;
                        }
                        plantryId = existing.Id.Value;
                    }
                    else
                    {
                        results.Add(new UnitCommitResult(
                            row.GrocyId, row.GrocyName,
                            Skipped: false, Success: false,
                            PlantryUnitId: null,
                            ErrorMessage: result.Error.Description));
                        continue;
                    }
                }

                crosswalkMappings[row.GrocyId.ToString()] = plantryId;
                results.Add(new UnitCommitResult(
                    row.GrocyId, row.GrocyName,
                    Skipped: false, Success: true,
                    PlantryUnitId: plantryId, ErrorMessage: null));
            }
            catch (Exception ex)
            {
                results.Add(new UnitCommitResult(
                    row.GrocyId, row.GrocyName,
                    Skipped: false, Success: false,
                    PlantryUnitId: null,
                    ErrorMessage: $"Unexpected error: {ex.Message}"));
            }
        }

        // Write the crosswalk sidecar
        var crosswalk = new UnitCrosswalk
        {
            CommittedAt = DateTimeOffset.UtcNow,
            Mappings    = crosswalkMappings,
        };
        var crosswalkPath = UnitCrosswalk.ResolvePath(manifestFilePath);
        await crosswalk.WriteAsync(crosswalkPath, ct);

        return (results, crosswalkPath);
    }
}
