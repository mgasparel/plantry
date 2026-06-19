namespace Plantry.Migration.Grocy;

/// <summary>
/// Status flags for a staged Grocy product. Multiple flags may be set simultaneously.
/// </summary>
[Flags]
public enum ProductStagingFlags
{
    None              = 0,

    /// <summary>A product with the same name already exists in the household catalog (UNIQUE(household_id, name) would collide).</summary>
    NameCollision     = 1 << 0,

    /// <summary>This product has a parent_product_id set (is a variant of another product).</summary>
    IsVariant         = 1 << 1,

    /// <summary>This product has at least one barcode in Grocy's product_barcodes table. Barcodes are not imported (no barcode field in Plantry catalog); this flag surfaces the loss.</summary>
    HasDroppedBarcode = 1 << 2,

    /// <summary>qu_id_purchase ≠ qu_id_stock and a unit conversion exists — a product SKU will be synthesized.</summary>
    IsMultiUnit       = 1 << 3,

    /// <summary>A required crosswalk entry (unit, category, or location) was missing — the Plantry id could not be resolved.</summary>
    CrosswalkMissing  = 1 << 4,
}

/// <summary>
/// A synthesized product SKU derived from the Grocy purchase-unit conversion.
/// Produced for multi-unit products where qu_id_purchase ≠ qu_id_stock and a conversion
/// factor (product-specific or global) exists.
/// </summary>
public sealed class StagedProductSku
{
    /// <summary>Display label — the purchase unit name (e.g. "Pack", "Case12").</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Pack size quantity (conversion factor from purchase unit to stock unit).</summary>
    public decimal SizeQuantity { get; set; }

    /// <summary>Grocy unit id of the stock unit used as the size unit reference.</summary>
    public int SizeUnitGrocyId { get; set; }

    /// <summary>
    /// Plantry unit GUID resolved from the stock-unit crosswalk. Null if the unit crosswalk
    /// entry is missing (CrosswalkMissing flag will be set on the staging row).
    /// </summary>
    public Guid? SizeUnitPlantryId { get; set; }
}

/// <summary>
/// A single staged Grocy product — the output of <see cref="ProductStager"/>
/// and the row model for the /Import/Products review screen (plantry-zcw.4).
///
/// Staging is read-only relative to the domain: no Plantry writes happen until zcw.5.
/// All fields are derived from the Grocy manifest + the three crosswalks (unit, category, location).
/// </summary>
public sealed class ProductStagingRow
{
    // ──────────── Grocy source identity ────────────────────────────────────

    /// <summary>Grocy product.id.</summary>
    public int GrocyId { get; set; }

    /// <summary>Grocy product.name (raw, before collision-suffix logic).</summary>
    public string GrocyName { get; set; } = string.Empty;

    /// <summary>Grocy product.parent_product_id — null for top-level products.</summary>
    public int? GrocyParentProductId { get; set; }

    /// <summary>Grocy product.product_group_id.</summary>
    public int? GrocyProductGroupId { get; set; }

    /// <summary>Grocy product.location_id.</summary>
    public int? GrocyLocationId { get; set; }

    /// <summary>Grocy product.qu_id_stock — used as the Plantry default_unit_id.</summary>
    public int GrocyQuIdStock { get; set; }

    /// <summary>Grocy product.qu_id_purchase — compared with qu_id_stock for multi-unit detection.</summary>
    public int GrocyQuIdPurchase { get; set; }

    // ──────────── Resolved Plantry target values ────────────────────────────

    /// <summary>
    /// Proposed Plantry product name. Normally equals GrocyName; a suffix is appended
    /// (e.g. " (Grocy)") when a NameCollision is detected.
    /// Editable on the review screen (inline htmx) so the user can resolve collisions
    /// without leaving the page.
    /// </summary>
    public string PlantryName { get; set; } = string.Empty;

    /// <summary>Resolved Plantry unit GUID from the unit crosswalk (qu_id_stock mapping). Null when CrosswalkMissing.</summary>
    public Guid? DefaultUnitId { get; set; }

    /// <summary>Human-readable unit name for display on the review grid.</summary>
    public string? DefaultUnitName { get; set; }

    /// <summary>Resolved Plantry category GUID from the category crosswalk. Null when product_group_id is null or CrosswalkMissing.</summary>
    public Guid? CategoryId { get; set; }

    /// <summary>Human-readable category name for display on the review grid.</summary>
    public string? CategoryName { get; set; }

    /// <summary>Resolved Plantry location GUID from the location crosswalk. Null when location_id is null or CrosswalkMissing.</summary>
    public Guid? DefaultLocationId { get; set; }

    /// <summary>Human-readable location name for display on the review grid.</summary>
    public string? DefaultLocationName { get; set; }

    // ──────────── Expiry defaults (sentinel-remapped) ──────────────────────

    /// <summary>
    /// Grocy default_best_before_days remapped: -1/0 → null (no default), positive pass-through.
    /// </summary>
    public int? DefaultDueDays { get; set; }

    /// <summary>Grocy default_best_before_days_after_open remapped: -1/0 → null.</summary>
    public int? DefaultDueDaysAfterOpening { get; set; }

    /// <summary>Grocy default_best_before_days_after_freezing remapped: -1/0 → null.</summary>
    public int? DefaultDueDaysAfterFreezing { get; set; }

    /// <summary>Grocy default_best_before_days_after_thawing remapped: -1/0 → null.</summary>
    public int? DefaultDueDaysAfterThawing { get; set; }

    // ──────────── SKU synthesis ────────────────────────────────────────────

    /// <summary>
    /// Synthesized SKU for multi-unit products (IsMultiUnit flag set).
    /// Null for single-unit products.
    /// </summary>
    public StagedProductSku? SynthesizedSku { get; set; }

    // ──────────── Variant chain ────────────────────────────────────────────

    /// <summary>
    /// Grocy id of this product's parent in the Plantry staging list. Set when GrocyParentProductId is non-null.
    /// Used by the review screen to group variants under their parent.
    /// </summary>
    public int? ParentGrocyId => GrocyParentProductId;

    // ──────────── User drop disposition ───────────────────────────────────────

    /// <summary>
    /// True when the user has explicitly marked this product as dropped on the review screen.
    /// Dropped products are skipped by <see cref="ProductCommitService"/> and recorded as null
    /// entries in the product-crosswalk.json so re-runs recognise them as intentionally skipped.
    /// </summary>
    public bool IsDropped { get; set; }

    // ──────────── Status flags ─────────────────────────────────────────────

    /// <summary>Combination of <see cref="ProductStagingFlags"/> describing any issues with this product.</summary>
    public ProductStagingFlags Flags { get; set; } = ProductStagingFlags.None;

    // ──────────── Convenience flag helpers ─────────────────────────────────

    public bool HasFlag(ProductStagingFlags flag) => (Flags & flag) == flag;
    public bool HasNameCollision     => HasFlag(ProductStagingFlags.NameCollision);
    public bool IsVariant            => HasFlag(ProductStagingFlags.IsVariant);
    public bool HasDroppedBarcode    => HasFlag(ProductStagingFlags.HasDroppedBarcode);
    public bool IsMultiUnit          => HasFlag(ProductStagingFlags.IsMultiUnit);
    public bool HasCrosswalkMissing  => HasFlag(ProductStagingFlags.CrosswalkMissing);

    /// <summary>True when any flag is set — the row will be highlighted on the review screen.</summary>
    public bool IsFlagged => Flags != ProductStagingFlags.None;

    /// <summary>Grocy row_created_timestamp parsed as DateTimeOffset, or null.</summary>
    public DateTimeOffset? CreatedAt { get; set; }
}
