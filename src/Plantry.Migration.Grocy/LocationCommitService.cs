using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Migration.Grocy;

/// <summary>
/// Executes the Location commit step of the Grocy import pipeline (grocy-import-plan.md §7).
///
/// For each staged location (non-Skipped):
///   - MatchExisting → find the Plantry location by name; raise if missing.
///   - CreateNew     → call CreateLocationCommand; treat DuplicateLocationName as idempotent.
///
/// Writes the grocy_location_id → plantry_location_id crosswalk JSON alongside the manifest.
/// The commit is idempotent: re-running after a partial failure picks up where it left off.
/// </summary>
public sealed class LocationCommitService(
    ILocationRepository locations,
    ITenantContext tenant)
{
    /// <summary>Result of a single location commit.</summary>
    public sealed record LocationCommitResult(
        int GrocyId,
        string GrocyName,
        bool Skipped,
        bool Success,
        Guid? PlantryLocationId,
        string? ErrorMessage);

    /// <summary>
    /// Commits all non-Skipped staging rows, writes the crosswalk, and returns per-row results.
    /// </summary>
    public async Task<(IReadOnlyList<LocationCommitResult> Results, string CrosswalkPath)> CommitAsync(
        IReadOnlyList<LocationStagingRow> stagingRows,
        string manifestFilePath,
        CancellationToken ct = default)
    {
        var results = new List<LocationCommitResult>(stagingRows.Count);
        var crosswalkMappings = new Dictionary<string, Guid>();

        foreach (var row in stagingRows)
        {
            if (row.Status == LocationStagingStatus.Skipped)
            {
                results.Add(new LocationCommitResult(
                    row.GrocyId, row.GrocyName,
                    Skipped: true, Success: true,
                    PlantryLocationId: null, ErrorMessage: null));
                continue;
            }

            try
            {
                Guid plantryId;

                if (row.Action == LocationMappingAction.MatchExisting)
                {
                    var existing = await locations.FindByNameAsync(row.PlantryName!, ct);
                    if (existing is null)
                    {
                        results.Add(new LocationCommitResult(
                            row.GrocyId, row.GrocyName,
                            Skipped: false, Success: false,
                            PlantryLocationId: null,
                            ErrorMessage: $"Plantry location named '{row.PlantryName}' not found."));
                        continue;
                    }
                    plantryId = existing.Id.Value;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(row.PlantryName))
                    {
                        results.Add(new LocationCommitResult(
                            row.GrocyId, row.GrocyName,
                            Skipped: false, Success: false,
                            PlantryLocationId: null,
                            ErrorMessage: "PlantryName is required for CreateNew action."));
                        continue;
                    }

                    var locationType = LocationTypeExtensions.Parse(row.LocationType);
                    var cmd = new CreateLocationCommand(row.PlantryName, locationType, locations, tenant);
                    var result = await cmd.ExecuteAsync(ct);

                    if (result.IsSuccess)
                    {
                        plantryId = result.Value.Value;
                    }
                    else if (result.Error.Code == "Catalog.DuplicateLocationName")
                    {
                        var existing = await locations.FindByNameAsync(row.PlantryName!, ct);
                        if (existing is null)
                        {
                            results.Add(new LocationCommitResult(
                                row.GrocyId, row.GrocyName,
                                Skipped: false, Success: false,
                                PlantryLocationId: null,
                                ErrorMessage: $"Location '{row.PlantryName}' reported as duplicate but could not be found."));
                            continue;
                        }
                        plantryId = existing.Id.Value;
                    }
                    else
                    {
                        results.Add(new LocationCommitResult(
                            row.GrocyId, row.GrocyName,
                            Skipped: false, Success: false,
                            PlantryLocationId: null,
                            ErrorMessage: result.Error.Description));
                        continue;
                    }
                }

                crosswalkMappings[row.GrocyId.ToString()] = plantryId;
                results.Add(new LocationCommitResult(
                    row.GrocyId, row.GrocyName,
                    Skipped: false, Success: true,
                    PlantryLocationId: plantryId, ErrorMessage: null));
            }
            catch (Exception ex)
            {
                results.Add(new LocationCommitResult(
                    row.GrocyId, row.GrocyName,
                    Skipped: false, Success: false,
                    PlantryLocationId: null,
                    ErrorMessage: $"Unexpected error: {ex.Message}"));
            }
        }

        var crosswalk = new LocationCrosswalk
        {
            CommittedAt = DateTimeOffset.UtcNow,
            Mappings    = crosswalkMappings,
        };
        var crosswalkPath = LocationCrosswalk.ResolvePath(manifestFilePath);
        await crosswalk.WriteAsync(crosswalkPath, ct);

        return (results, crosswalkPath);
    }
}
