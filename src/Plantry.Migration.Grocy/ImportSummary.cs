namespace Plantry.Migration.Grocy;

/// <summary>
/// Aggregated staging counts for the pre-commit summary page (/Import/Summary, plantry-zcw.8).
///
/// Produced by <see cref="ImportSummaryService"/> from a complete staging pass
/// (no Plantry DB writes). All counts reflect what <em>would</em> be committed
/// if the user proceeds.
/// </summary>
public sealed class ImportSummary
{
    // ──────────── Units ───────────────────────────────────────────────────────

    /// <summary>Units that would be matched to an existing seeded Plantry unit (MatchExisting action, not Skipped).</summary>
    public int UnitsMatched { get; init; }

    /// <summary>Units that would be created as new Plantry units (CreateNew action, not Skipped).</summary>
    public int UnitsCreated { get; init; }

    /// <summary>Units intentionally skipped (Skipped status).</summary>
    public int UnitsSkipped { get; init; }

    /// <summary>Units with anomaly notes flagged for user review.</summary>
    public int UnitsAnomalyCount { get; init; }

    // ──────────── Categories ──────────────────────────────────────────────────

    /// <summary>Product groups that would be matched to an existing Plantry category.</summary>
    public int CategoriesMatched { get; init; }

    /// <summary>Product groups that would create a new Plantry category.</summary>
    public int CategoriesCreated { get; init; }

    /// <summary>Product groups intentionally skipped.</summary>
    public int CategoriesSkipped { get; init; }

    // ──────────── Locations ───────────────────────────────────────────────────

    /// <summary>Grocy locations that would be matched to an existing Plantry location.</summary>
    public int LocationsMatched { get; init; }

    /// <summary>Grocy locations that would create a new Plantry location.</summary>
    public int LocationsCreated { get; init; }

    /// <summary>Grocy locations intentionally skipped.</summary>
    public int LocationsSkipped { get; init; }

    // ──────────── Products ────────────────────────────────────────────────────

    /// <summary>Total staged products (all, including flagged/skipped).</summary>
    public int ProductsTotal { get; init; }

    /// <summary>Products that are variants of a parent product (IsVariant flag).</summary>
    public int ProductsVariants { get; init; }

    /// <summary>Products with any staging flag set (name collision, barcode drop, crosswalk missing, etc.).</summary>
    public int ProductsFlagged { get; init; }

    // ──────────── Recipes ─────────────────────────────────────────────────────

    /// <summary>Total staged recipes.</summary>
    public int RecipesTotal { get; init; }

    /// <summary>Recipes that had one or more sub-recipe nestings flattened into them.</summary>
    public int RecipesWithFlattenedNestings { get; init; }

    /// <summary>Recipes that have a fetched photo in the manifest.</summary>
    public int RecipesWithPhotos { get; init; }

    // ──────────── Manifest metadata ───────────────────────────────────────────

    /// <summary>When the manifest was extracted from Grocy.</summary>
    public DateTimeOffset ManifestExtractedAt { get; init; }

    // ──────────── Crosswalk availability ──────────────────────────────────────

    /// <summary>Whether the unit crosswalk was found (units have been committed).</summary>
    public bool UnitCrosswalkFound { get; init; }

    /// <summary>Whether the category crosswalk was found (categories have been committed).</summary>
    public bool CategoryCrosswalkFound { get; init; }

    /// <summary>Whether the location crosswalk was found (locations have been committed).</summary>
    public bool LocationCrosswalkFound { get; init; }

    /// <summary>Whether the product crosswalk was found (products have been committed).</summary>
    public bool ProductCrosswalkFound { get; init; }
}

/// <summary>
/// A single tradeoff log entry (§8 of grocy-import-plan.md, T1–T15).
/// Rendered inline on the summary page so the user explicitly acknowledges each fidelity loss.
/// </summary>
public sealed record TradeoffEntry(
    string Id,
    string Title,
    string Description);

/// <summary>
/// The complete §8 tradeoff log (T1–T15), rendered on /Import/Summary.
/// Static — these are fixed design decisions baked into the import pipeline.
/// </summary>
public static class TradeoffLog
{
    /// <summary>All T1–T15 tradeoff entries in order.</summary>
    public static readonly IReadOnlyList<TradeoffEntry> All =
    [
        new("T1",  "Unit role collapse",
            "qu_id_stock chosen as default_unit_id — the four Grocy unit roles (stock, purchase, price, display) are collapsed to the single stock unit. Purchase-unit packaging is captured as a product SKU where qu_id_purchase ≠ qu_id_stock."),

        new("T2",  "track_stock default",
            "track_stock defaulted to true for all imported products — Grocy has no direct Plantry equivalent; all products are tracked."),

        new("T3",  "min_stock_amount dropped",
            "min_stock_amount dropped — Plantry has no minimum-stock concept; this field is not imported."),

        new("T4",  "Product photos dropped",
            "Product photos are not imported — the Plantry catalog has no image field. Recipe photos are preserved."),

        new("T5",  "not_check_stock_fulfillment_for_recipes dropped",
            "not_check_stock_fulfillment_for_recipes dropped — Plantry has no equivalent flag; all imported recipes will participate in stock fulfillment checks."),

        new("T6",  "Barcodes dropped",
            "Barcodes dropped for 33 products — the Plantry catalog has no barcode field. Products with dropped barcodes are flagged on the product review screen."),

        new("T7",  "oz / Cup factor drift",
            "Grocy stores oz = 28.35 g and Cup = 237 ml; Plantry seed values are 28.3495 g and 240 ml. Plantry seed wins. Cup drift is +1.3%; override to 237 on the unit grid if your recipe data uses Grocy's value."),

        new("T8",  "tsp / tbsp anomaly",
            "Grocy factors appear swapped: tsp stored as ~14.79 ml (≈ tbsp) and tbsp as ~17.76 ml (~20% off). Both are corrected to Plantry's canonical values (tsp = 4.92892 ml, tbsp = 14.7868 ml). Override on the unit grid only if your recipes were entered using Grocy's wrong values."),

        new("T9",  "½ Cup and ¼ Cup dropped",
            "1/2 Cup and 1/4 Cup are redundant fraction units — they are skipped. Use cup × 0.5 or cup × 0.25 in recipes instead."),

        new("T10", "Location description dropped",
            "Grocy location.description is dropped — Plantry locations have no description field."),

        new("T11", "Shopping locations not committed",
            "Shopping locations (e.g. Costco, Superstore, Metro) were extracted to the manifest but are not committed — the Pricing bounded context is not yet built. They are parked for a future migration."),

        new("T12", "Produces-product links dropped",
            "21 Grocy recipes have a product_id (\"produces a product\") link. The link is dropped; a provenance note is appended to the recipe source URL field instead."),

        new("T13", "Recipe nestings flattened",
            "16 nesting edges across normal recipes are flattened — sub-recipe ingredients are scaled and inserted into the parent recipe under a group heading named after the sub-recipe. No Plantry recipe nesting concept exists."),

        new("T14", "Redundant product-specific conversions dropped",
            "Product-specific conversions that duplicate a global conversion factor (same from/to units, same factor) are dropped — the global conversion handles them."),

        new("T15", "desired_servings dropped",
            "desired_servings dropped — Plantry's ServingsScale is a transient UI state, not a persisted field."),
    ];
}
