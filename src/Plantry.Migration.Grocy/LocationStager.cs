namespace Plantry.Migration.Grocy;

/// <summary>
/// Stages all Grocy locations from the manifest into <see cref="LocationStagingRow"/> records.
///
/// Algorithm (per grocy-import-plan.md §4 and zcw.9 scope):
/// 1. is_freezer == 1 → LocationType = "frozen"; else → LocationType = "ambient".
/// 2. Case-insensitive exact name match against existing Plantry location names → MatchExisting, Auto.
/// 3. Contains match (either direction) → MatchExisting, Auto (first candidate wins).
/// 4. Ambiguous (multiple candidates) → MatchExisting, NeedsReview, AnomalyNote lists candidates.
/// 5. No match → CreateNew, PlantryName = Grocy name trimmed, NeedsReview.
/// </summary>
public static class LocationStager
{
    /// <summary>
    /// Stages all locations from the manifest and returns the staging rows in Grocy-id order.
    /// </summary>
    /// <param name="manifest">The Grocy manifest snapshot.</param>
    /// <param name="existingLocationNames">
    /// The names of Plantry locations already in the database — used for name-matching
    /// and for the MatchExisting action.
    /// </param>
    public static IReadOnlyList<LocationStagingRow> Stage(
        GrocyManifest manifest,
        IReadOnlyList<string> existingLocationNames)
    {
        var rows = new List<LocationStagingRow>(manifest.Locations.Count);

        foreach (var location in manifest.Locations)
        {
            var nameTrimmed  = location.Name.Trim();
            var isFreezer    = location.IsFreezer == 1;
            var locationType = isFreezer ? "frozen" : "ambient";

            // Exact name match (case-insensitive)
            var exactMatch = existingLocationNames
                .FirstOrDefault(n => string.Equals(n, nameTrimmed, StringComparison.OrdinalIgnoreCase));

            if (exactMatch is not null)
            {
                rows.Add(new LocationStagingRow
                {
                    GrocyId      = location.Id,
                    GrocyName    = location.Name,
                    IsFreezer    = isFreezer,
                    PlantryName  = exactMatch,
                    LocationType = locationType,
                    Action       = LocationMappingAction.MatchExisting,
                    Status       = LocationStagingStatus.Auto,
                    AnomalyNote  = null,
                });
                continue;
            }

            // Fuzzy contains match
            var candidates = existingLocationNames
                .Where(n =>
                    n.Contains(nameTrimmed, StringComparison.OrdinalIgnoreCase) ||
                    nameTrimmed.Contains(n, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 1)
            {
                rows.Add(new LocationStagingRow
                {
                    GrocyId      = location.Id,
                    GrocyName    = location.Name,
                    IsFreezer    = isFreezer,
                    PlantryName  = candidates[0],
                    LocationType = locationType,
                    Action       = LocationMappingAction.MatchExisting,
                    Status       = LocationStagingStatus.Auto,
                    AnomalyNote  = null,
                });
            }
            else if (candidates.Count > 1)
            {
                rows.Add(new LocationStagingRow
                {
                    GrocyId      = location.Id,
                    GrocyName    = location.Name,
                    IsFreezer    = isFreezer,
                    PlantryName  = candidates[0],
                    LocationType = locationType,
                    Action       = LocationMappingAction.MatchExisting,
                    Status       = LocationStagingStatus.NeedsReview,
                    AnomalyNote  = $"Multiple Plantry locations match '{nameTrimmed}': " +
                                   string.Join(", ", candidates.Select(c => $"'{c}'")),
                });
            }
            else
            {
                // No match — propose creating a new location
                rows.Add(new LocationStagingRow
                {
                    GrocyId      = location.Id,
                    GrocyName    = location.Name,
                    IsFreezer    = isFreezer,
                    PlantryName  = nameTrimmed,
                    LocationType = locationType,
                    Action       = LocationMappingAction.CreateNew,
                    Status       = LocationStagingStatus.NeedsReview,
                    AnomalyNote  = $"No existing Plantry location matches '{nameTrimmed}'. " +
                                   "A new location will be created — adjust the name or type if needed.",
                });
            }
        }

        return rows.OrderBy(r => r.GrocyId).ToList();
    }
}
