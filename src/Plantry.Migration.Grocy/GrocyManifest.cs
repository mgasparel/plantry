using Plantry.Migration.Grocy.Dto;

namespace Plantry.Migration.Grocy;

/// <summary>
/// Immutable, versioned JSON snapshot of all Grocy collections in scope for the import pipeline.
/// Written by <see cref="ExtractCommand"/> as a single JSON file; re-runnable (overwrites).
/// Modelled on §2 of the grocy-import-plan.md (Extract stage): decoupling extraction from staging
/// means the rest of the pipeline never depends on Grocy being online.
/// </summary>
public sealed record GrocyManifest
{
    /// <summary>Schema version for forward-compatibility checks.</summary>
    public string Version { get; init; } = "1.0";

    /// <summary>UTC timestamp when the manifest was written.</summary>
    public DateTimeOffset ExtractedAt { get; init; }

    public IReadOnlyList<GrocyProduct> Products { get; init; } = [];
    public IReadOnlyList<GrocyQuantityUnit> QuantityUnits { get; init; } = [];
    public IReadOnlyList<GrocyQuantityUnitConversion> QuantityUnitConversions { get; init; } = [];
    public IReadOnlyList<GrocyLocation> Locations { get; init; } = [];
    public IReadOnlyList<GrocyProductGroup> ProductGroups { get; init; } = [];

    /// <summary>
    /// Only recipes with <c>type == "normal"</c>; meal-plan artifacts are filtered out client-side
    /// during extraction (§3 — 65 of 415 total).
    /// </summary>
    public IReadOnlyList<GrocyRecipe> Recipes { get; init; } = [];

    /// <summary>Ingredient positions belonging to the in-scope normal recipes only.</summary>
    public IReadOnlyList<GrocyRecipePosition> RecipePositions { get; init; } = [];

    /// <summary>Nesting edges belonging to the in-scope normal recipes only.</summary>
    public IReadOnlyList<GrocyRecipeNesting> RecipeNestings { get; init; } = [];

    public IReadOnlyList<GrocyUserfield> Userfields { get; init; } = [];
    public IReadOnlyList<GrocyProductBarcode> ProductBarcodes { get; init; } = [];

    /// <summary>
    /// Per-recipe userfield values (recipe_id → original_recipe URL).
    /// Only recipes where the userfield is set are included.
    /// </summary>
    public IReadOnlyList<GrocyRecipeUserfield> RecipeUserfields { get; init; } = [];

    /// <summary>
    /// Fetched recipe photo bytes, keyed by recipe id.
    /// Only the 16 recipes with a <c>picture_file_name</c> are included.
    /// </summary>
    public IReadOnlyList<GrocyRecipePhoto> RecipePhotos { get; init; } = [];
}

/// <summary>
/// Per-collection row counts derived from a <see cref="GrocyManifest"/>.
/// Lives in <c>Plantry.Migration.Grocy</c> rather than the web layer so it can be tested
/// without a web application factory.
/// </summary>
public sealed record ManifestCounts(
    int Products,
    int QuantityUnits,
    int QuantityUnitConversions,
    int Locations,
    int ProductGroups,
    int Recipes,
    int RecipePositions,
    int RecipeNestings,
    int Userfields,
    int ProductBarcodes,
    int RecipeUserfields,
    int RecipePhotos)
{
    public static ManifestCounts From(GrocyManifest m) => new(
        m.Products.Count,
        m.QuantityUnits.Count,
        m.QuantityUnitConversions.Count,
        m.Locations.Count,
        m.ProductGroups.Count,
        m.Recipes.Count,
        m.RecipePositions.Count,
        m.RecipeNestings.Count,
        m.Userfields.Count,
        m.ProductBarcodes.Count,
        m.RecipeUserfields.Count,
        m.RecipePhotos.Count);
}
