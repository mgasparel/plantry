namespace Plantry.Migration.Grocy;

/// <summary>
/// The staging disposition for a Grocy product group (category) mapping.
/// </summary>
public enum CategoryStagingStatus
{
    /// <summary>Auto-mapped with high confidence (name match).</summary>
    Auto,

    /// <summary>Assigned by algorithm but flagged for user review.</summary>
    NeedsReview,

    /// <summary>User has explicitly confirmed or adjusted the mapping.</summary>
    Mapped,

    /// <summary>Category is intentionally skipped.</summary>
    Skipped,
}

/// <summary>
/// The resolved mapping action for a staged category: whether to match an existing
/// Plantry category or create a new one.
/// </summary>
public enum CategoryMappingAction
{
    /// <summary>Point to an existing Plantry category (matched by name).</summary>
    MatchExisting,

    /// <summary>Create a new Plantry category from the proposed name.</summary>
    CreateNew,
}

/// <summary>
/// A single staged Grocy product group — the output of the category staging algorithm
/// and the row model for the /Import/Categories mapping grid.
/// </summary>
public sealed class CategoryStagingRow
{
    // ──────────── Grocy source ─────────────────────────────────────────────

    /// <summary>Grocy product_group.id.</summary>
    public int GrocyId { get; set; }

    /// <summary>Grocy product_group.name.</summary>
    public string GrocyName { get; set; } = string.Empty;

    // ──────────── Assigned mapping ──────────────────────────────────────────

    /// <summary>Proposed Plantry category name.</summary>
    public string? PlantryName { get; set; }

    /// <summary>Whether to match an existing category or create a new one.</summary>
    public CategoryMappingAction Action { get; set; } = CategoryMappingAction.CreateNew;

    /// <summary>Staging confidence status.</summary>
    public CategoryStagingStatus Status { get; set; } = CategoryStagingStatus.Auto;

    /// <summary>Human-readable anomaly note. Null when no anomaly.</summary>
    public string? AnomalyNote { get; set; }
}
