namespace Plantry.Migration.Grocy;

/// <summary>
/// The mapping disposition for a staged Grocy unit — what the staging algorithm decided
/// and whether the user has confirmed it.
/// </summary>
public enum UnitStagingStatus
{
    /// <summary>Auto-mapped with high confidence (seed match or unambiguous graph component).</summary>
    Auto,

    /// <summary>Assigned by algorithm but flagged for user review (anomaly, fraction collapse, isolated unit).</summary>
    NeedsReview,

    /// <summary>User has explicitly confirmed or adjusted the mapping.</summary>
    Mapped,

    /// <summary>Unit is intentionally dropped (e.g. 1/2 Cup, 1/4 Cup — redundant fractions).</summary>
    Skipped,
}

/// <summary>
/// The resolved mapping action for a staged unit: whether to match an existing seeded
/// unit or create a new one.
/// </summary>
public enum UnitMappingAction
{
    /// <summary>Point to an existing Plantry unit (matched by code).</summary>
    MatchExisting,

    /// <summary>Create a new Plantry unit from the proposed code/name/dimension/factor.</summary>
    CreateNew,
}

/// <summary>
/// A single staged Grocy quantity unit — the output of <see cref="UnitStager"/>
/// and the row model for the /Import/Units mapping grid.
///
/// The staging algorithm runs on manifest load; the page model exposes these to the
/// user for confirm/override; the commit handler iterates them to match or create
/// Plantry units and write the crosswalk.
/// </summary>
public sealed class UnitStagingRow
{
    // ──────────── Grocy source ─────────────────────────────────────────────

    /// <summary>Grocy quantity_unit.id.</summary>
    public int GrocyId { get; set; }

    /// <summary>Grocy quantity_unit.name (display label).</summary>
    public string GrocyName { get; set; } = string.Empty;

    // ──────────── Assigned mapping ──────────────────────────────────────────

    /// <summary>Dimension assigned by the algorithm or confirmed by the user.</summary>
    public string Dimension { get; set; } = "count";

    /// <summary>
    /// Proposed Plantry unit code: matches a seeded unit (e.g. "g") or a new code
    /// for creation (e.g. "pt"). Null only for Skipped rows.
    /// </summary>
    public string? PlantryCode { get; set; }

    /// <summary>Proposed Plantry unit display name (used only when Action is CreateNew).</summary>
    public string? PlantryName { get; set; }

    /// <summary>
    /// factor_to_base for the assigned dimension: multiply this unit × factor to reach
    /// the base unit of the dimension (g for mass, ml for volume, ea for count).
    /// </summary>
    public decimal FactorToBase { get; set; } = 1m;

    /// <summary>Whether to match an existing unit by code or create a new one.</summary>
    public UnitMappingAction Action { get; set; } = UnitMappingAction.MatchExisting;

    /// <summary>
    /// Staging confidence: Auto (algorithm resolved cleanly), NeedsReview (anomaly or
    /// low confidence), Mapped (user confirmed), Skipped (dropped).
    /// </summary>
    public UnitStagingStatus Status { get; set; } = UnitStagingStatus.Auto;

    // ──────────── Anomaly flags (displayed on the grid) ────────────────────

    /// <summary>
    /// Human-readable anomaly note pre-filled by the staging algorithm.
    /// Null when no anomaly was detected.
    ///
    /// Examples: "Grocy stored 14.7867 (≈ tablespoon) — anomalous; using Plantry's 4.92892"
    ///           "Grocy stored 237 ml for cup; using Plantry's 240 ml (+1.3% drift)"
    ///           "Redundant fraction — dropping; use cup × 0.5 instead"
    /// </summary>
    public string? AnomalyNote { get; set; }
}
