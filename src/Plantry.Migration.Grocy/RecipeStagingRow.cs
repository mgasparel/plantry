namespace Plantry.Migration.Grocy;

/// <summary>
/// Status flags for a staged Grocy recipe. Multiple flags may be set simultaneously.
/// </summary>
[Flags]
public enum RecipeStagingFlags
{
    None               = 0,

    /// <summary>A recipe with the same name already exists in the household (UNIQUE(household_id, name) would collide).</summary>
    NameCollision      = 1 << 0,

    /// <summary>
    /// One or more sub-recipe nestings were flattened into this recipe (plan §5.3).
    /// The sub-recipe's ingredients were scaled and inserted under a group_heading.
    /// </summary>
    HasFlattenedNesting = 1 << 1,

    /// <summary>
    /// One or more ingredient rows had a non-empty <c>note</c> field.
    /// Notes are dropped (no per-ingredient note in Plantry); flagged so the user
    /// can fold important notes into directions manually (plan §5.4 / §8-T15).
    /// </summary>
    HasDroppedNotes    = 1 << 2,

    /// <summary>
    /// This recipe has a <c>product_id</c> set (the "produces a product" link).
    /// The link is dropped; a provenance text is appended to source (plan §5.2 / §8-T12).
    /// </summary>
    HasProducesProduct = 1 << 3,

    /// <summary>
    /// A required crosswalk entry (product or unit) was missing for at least one ingredient.
    /// The ingredient is staged without a resolved Plantry ID; commit will skip it.
    /// </summary>
    CrosswalkMissing   = 1 << 4,
}

/// <summary>
/// A single staged Grocy recipe ingredient — child of <see cref="RecipeStagingRow"/>.
/// Quantity/unit are always present (Grocy guarantees it for all recipe positions —
/// plan §5.4: "every normal-recipe row has product_id, qu_id, and amount").
/// </summary>
public sealed class StagedIngredient
{
    // ── Grocy source values ────────────────────────────────────────────────

    /// <summary>Grocy recipes_pos.id (0 for synthesized/flattened rows).</summary>
    public int GrocyPositionId { get; set; }

    /// <summary>Grocy product_id (required by recipe position).</summary>
    public int GrocyProductId { get; set; }

    /// <summary>Grocy quantity unit id (required).</summary>
    public int GrocyUnitId { get; set; }

    /// <summary>Amount (required, always > 0).</summary>
    public decimal Amount { get; set; }

    /// <summary>Grocy ingredient_group — becomes group_heading on the Plantry ingredient.</summary>
    public string? IngredientGroup { get; set; }

    /// <summary>Grocy note field (8 across all normal recipes) — dropped, surfaced by HasDroppedNotes flag.</summary>
    public string? DroppedNote { get; set; }

    // ── Resolved Plantry values ────────────────────────────────────────────

    /// <summary>Resolved Plantry product GUID from the product crosswalk. Null when CrosswalkMissing.</summary>
    public Guid? PlantryProductId { get; set; }

    /// <summary>Human-readable product name for review display.</summary>
    public string? ProductName { get; set; }

    /// <summary>Resolved Plantry unit GUID from the unit crosswalk. Null when CrosswalkMissing.</summary>
    public Guid? PlantryUnitId { get; set; }

    /// <summary>Human-readable unit name for review display.</summary>
    public string? UnitName { get; set; }

    /// <summary>
    /// Group heading used on the Plantry ingredient row.
    /// Normally mirrors IngredientGroup; for flattened nesting rows this is set to the
    /// sub-recipe's name (e.g. "Caesar Dressing").
    /// </summary>
    public string? GroupHeading { get; set; }

    /// <summary>Display ordinal (0-based, set by RecipeStager during staging).</summary>
    public int Ordinal { get; set; }

    /// <summary>True when this ingredient was flattened in from a sub-recipe nesting.</summary>
    public bool IsFromNesting { get; set; }
}

/// <summary>
/// A single staged Grocy recipe — the output of <see cref="RecipeStager"/>
/// and the row model for the /Import/Recipes review screen (plantry-zcw.6).
///
/// Staging is read-only relative to the domain: no Plantry writes happen until zcw.7.
/// All fields are derived from the Grocy manifest + the product and unit crosswalks.
/// </summary>
public sealed class RecipeStagingRow
{
    // ── Grocy source identity ──────────────────────────────────────────────

    /// <summary>Grocy recipe.id.</summary>
    public int GrocyId { get; set; }

    /// <summary>Grocy recipe.name (raw, before collision-suffix logic).</summary>
    public string GrocyName { get; set; } = string.Empty;

    /// <summary>Grocy recipe.base_servings.</summary>
    public int BaseServings { get; set; }

    /// <summary>Grocy recipe.product_id (null for most recipes; 21 have one).</summary>
    public int? GrocyProductId { get; set; }

    /// <summary>Grocy recipe.picture_file_name (null for recipes without a photo).</summary>
    public string? PictureFileName { get; set; }

    // ── Resolved Plantry target values ────────────────────────────────────

    /// <summary>
    /// Proposed Plantry recipe name. Normally equals GrocyName; suffix appended on collision.
    /// </summary>
    public string PlantryName { get; set; } = string.Empty;

    /// <summary>Converted (HTML→text) directions text. Null if the source description was empty/null.</summary>
    public string? Directions { get; set; }

    /// <summary>
    /// Recipe source URL from the userfields.original_recipe userfield.
    /// May be augmented with a "produces: ProductName" provenance line when HasProducesProduct.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>Grocy row_created_timestamp parsed as DateTimeOffset, or null.</summary>
    public DateTimeOffset? CreatedAt { get; set; }

    // ── Ingredients ────────────────────────────────────────────────────────

    /// <summary>
    /// Staged ingredients (after nesting flattening). Ordered by Ordinal.
    /// May include flattened sub-recipe ingredients (IsFromNesting == true).
    /// </summary>
    public IReadOnlyList<StagedIngredient> Ingredients { get; set; } = [];

    // ── Photo ──────────────────────────────────────────────────────────────

    /// <summary>Fetched photo bytes. Null if the recipe has no photo or the fetch failed.</summary>
    public byte[]? PhotoBytes { get; set; }

    /// <summary>Photo content-type (e.g. "image/jpeg"). Null when PhotoBytes is null.</summary>
    public string? PhotoContentType { get; set; }

    // ── Nesting metadata (for review screen display) ───────────────────────

    /// <summary>
    /// Names of sub-recipes that were flattened into this recipe.
    /// One entry per nesting edge. Empty when HasFlattenedNesting is false.
    /// </summary>
    public IReadOnlyList<string> FlattenedSubRecipeNames { get; set; } = [];

    // ── Status flags ───────────────────────────────────────────────────────

    /// <summary>Combination of <see cref="RecipeStagingFlags"/> describing any issues with this recipe.</summary>
    public RecipeStagingFlags Flags { get; set; } = RecipeStagingFlags.None;

    // ── Convenience flag helpers ───────────────────────────────────────────

    public bool HasFlag(RecipeStagingFlags flag) => (Flags & flag) == flag;
    public bool HasNameCollision       => HasFlag(RecipeStagingFlags.NameCollision);
    public bool HasFlattenedNesting    => HasFlag(RecipeStagingFlags.HasFlattenedNesting);
    public bool HasDroppedNotes        => HasFlag(RecipeStagingFlags.HasDroppedNotes);
    public bool HasProducesProduct     => HasFlag(RecipeStagingFlags.HasProducesProduct);
    public bool HasCrosswalkMissing    => HasFlag(RecipeStagingFlags.CrosswalkMissing);

    /// <summary>True when any flag is set — the row will be highlighted on the review screen.</summary>
    public bool IsFlagged => Flags != RecipeStagingFlags.None;
}
