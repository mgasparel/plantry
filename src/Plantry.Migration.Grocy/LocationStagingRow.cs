namespace Plantry.Migration.Grocy;

/// <summary>
/// The staging disposition for a Grocy location mapping.
/// </summary>
public enum LocationStagingStatus
{
    /// <summary>Auto-mapped with high confidence (name match).</summary>
    Auto,

    /// <summary>Assigned by algorithm but flagged for user review.</summary>
    NeedsReview,

    /// <summary>User has explicitly confirmed or adjusted the mapping.</summary>
    Mapped,

    /// <summary>Location is intentionally skipped.</summary>
    Skipped,
}

/// <summary>
/// The resolved mapping action for a staged location.
/// </summary>
public enum LocationMappingAction
{
    /// <summary>Point to an existing Plantry location (matched by name).</summary>
    MatchExisting,

    /// <summary>Create a new Plantry location from the proposed name and type.</summary>
    CreateNew,
}

/// <summary>
/// A single staged Grocy location — the output of the location staging algorithm
/// and the row model for the /Import/Locations mapping grid.
/// </summary>
public sealed class LocationStagingRow
{
    // ──────────── Grocy source ─────────────────────────────────────────────

    /// <summary>Grocy location.id.</summary>
    public int GrocyId { get; set; }

    /// <summary>Grocy location.name.</summary>
    public string GrocyName { get; set; } = string.Empty;

    /// <summary>Whether Grocy marks this location as a freezer (is_freezer == 1).</summary>
    public bool IsFreezer { get; set; }

    // ──────────── Assigned mapping ──────────────────────────────────────────

    /// <summary>Proposed Plantry location name.</summary>
    public string? PlantryName { get; set; }

    /// <summary>Proposed Plantry location type ("ambient" or "frozen").</summary>
    public string LocationType { get; set; } = "ambient";

    /// <summary>Whether to match an existing location or create a new one.</summary>
    public LocationMappingAction Action { get; set; } = LocationMappingAction.CreateNew;

    /// <summary>Staging confidence status.</summary>
    public LocationStagingStatus Status { get; set; } = LocationStagingStatus.Auto;

    /// <summary>Human-readable anomaly note. Null when no anomaly.</summary>
    public string? AnomalyNote { get; set; }
}
