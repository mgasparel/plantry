namespace Plantry.Migration.Grocy;

/// <summary>
/// Stages all Grocy product groups from the manifest into <see cref="CategoryStagingRow"/> records.
///
/// Algorithm (per grocy-import-plan.md §4 and zcw.9 scope):
/// 1. Known exact/alias table: a curated set of Grocy product group names → Plantry category names.
///    Groups with multiple Grocy sources collapsing to one Plantry category are flagged NeedsReview.
/// 2. Fuzzy fallback: case-insensitive contains match against the provided existing Plantry category names.
///    First match wins; NeedsReview if ambiguous (more than one candidate).
/// 3. Unmatched groups → Action = CreateNew, PlantryName = Grocy name trimmed.
/// </summary>
public static class CategoryStager
{
    // ──────────── Known alias table ───────────────────────────────────────────
    // Maps Grocy product_group.name (lower-case, trimmed) → (Plantry category name, NeedsReview)
    // Entries in this table override the fuzzy pass.

    private sealed record AliasEntry(string PlantryName, bool NeedsReview = false, string? AnomalyNote = null);

    private static readonly Dictionary<string, AliasEntry> KnownAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Direct mappings
            ["fruit & veg"]          = new("Fruits and Vegetables"),
            ["fruit and veg"]        = new("Fruits and Vegetables"),
            ["fruits & veg"]         = new("Fruits and Vegetables"),
            ["fruits and veg"]       = new("Fruits and Vegetables"),
            ["frozen food"]          = new("Frozen"),
            ["frozen foods"]         = new("Frozen"),
            ["meat"]                 = new("Meat & Fish"),
            ["meat & fish"]          = new("Meat & Fish"),
            ["drinks"]               = new("Drinks"),
            ["beverages"]            = new("Drinks"),
            ["condiments"]           = new("Condiments"),
            ["condiment"]            = new("Condiments"),
            ["herbs"]                = new("Herbs and Spices"),
            ["herb"]                 = new("Herbs and Spices"),

            // Two Grocy groups collapse to one Plantry category — flag NeedsReview
            ["spices"]               = new("Herbs and Spices", NeedsReview: true,
                                           "Grocy 'Spices' and 'Herbs' both map to 'Herbs and Spices'. " +
                                           "Confirm the mapping or create a separate 'Spices' category."),
            ["spice"]                = new("Herbs and Spices", NeedsReview: true,
                                           "Grocy 'Spice' and 'Herbs' both map to 'Herbs and Spices'. " +
                                           "Confirm the mapping or create a separate 'Spices' category."),

            // Create-new groups
            ["prepared (homemade)"]  = null!,   // handled specially below
            ["prepared"]             = null!,   // handled specially below
            ["homemade"]             = null!,   // handled specially below
        };

    // Groups explicitly mapped to CreateNew (Grocy name, lower-case).
    private static readonly HashSet<string> ExplicitCreateNew =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "prepared (homemade)",
            "prepared",
            "homemade",
        };

    /// <summary>
    /// Stages all product groups from the manifest and returns the staging rows in Grocy-id order.
    /// </summary>
    /// <param name="manifest">The Grocy manifest snapshot.</param>
    /// <param name="existingCategoryNames">
    /// The names of Plantry categories already in the database — used for fuzzy name-matching
    /// and for the MatchExisting action.
    /// </param>
    public static IReadOnlyList<CategoryStagingRow> Stage(
        GrocyManifest manifest,
        IReadOnlyList<string> existingCategoryNames)
    {
        var rows = new List<CategoryStagingRow>(manifest.ProductGroups.Count);

        foreach (var group in manifest.ProductGroups)
        {
            var nameTrimmed = group.Name.Trim();

            // 1. Explicit create-new override
            if (ExplicitCreateNew.Contains(nameTrimmed))
            {
                rows.Add(new CategoryStagingRow
                {
                    GrocyId     = group.Id,
                    GrocyName   = group.Name,
                    PlantryName = nameTrimmed,
                    Action      = CategoryMappingAction.CreateNew,
                    Status      = CategoryStagingStatus.Auto,
                    AnomalyNote = null,
                });
                continue;
            }

            // 2. Known alias table
            if (KnownAliases.TryGetValue(nameTrimmed, out var alias))
            {
                rows.Add(new CategoryStagingRow
                {
                    GrocyId     = group.Id,
                    GrocyName   = group.Name,
                    PlantryName = alias.PlantryName,
                    Action      = CategoryMappingAction.MatchExisting,
                    Status      = alias.NeedsReview
                                    ? CategoryStagingStatus.NeedsReview
                                    : CategoryStagingStatus.Auto,
                    AnomalyNote = alias.AnomalyNote,
                });
                continue;
            }

            // 3. Fuzzy fallback: case-insensitive contains against existing Plantry category names
            var candidates = existingCategoryNames
                .Where(c =>
                    c.Contains(nameTrimmed, StringComparison.OrdinalIgnoreCase) ||
                    nameTrimmed.Contains(c, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 1)
            {
                rows.Add(new CategoryStagingRow
                {
                    GrocyId     = group.Id,
                    GrocyName   = group.Name,
                    PlantryName = candidates[0],
                    Action      = CategoryMappingAction.MatchExisting,
                    Status      = CategoryStagingStatus.Auto,
                    AnomalyNote = null,
                });
            }
            else if (candidates.Count > 1)
            {
                // Ambiguous: multiple Plantry categories match — flag for review, pick first
                rows.Add(new CategoryStagingRow
                {
                    GrocyId     = group.Id,
                    GrocyName   = group.Name,
                    PlantryName = candidates[0],
                    Action      = CategoryMappingAction.MatchExisting,
                    Status      = CategoryStagingStatus.NeedsReview,
                    AnomalyNote = $"Multiple Plantry categories match '{nameTrimmed}': " +
                                  string.Join(", ", candidates.Select(c => $"'{c}'")),
                });
            }
            else
            {
                // No match found — propose creating a new category
                rows.Add(new CategoryStagingRow
                {
                    GrocyId     = group.Id,
                    GrocyName   = group.Name,
                    PlantryName = nameTrimmed,
                    Action      = CategoryMappingAction.CreateNew,
                    Status      = CategoryStagingStatus.NeedsReview,
                    AnomalyNote = $"No existing Plantry category matches '{nameTrimmed}'. " +
                                  "A new category will be created — adjust the name if needed.",
                });
            }
        }

        return rows.OrderBy(r => r.GrocyId).ToList();
    }
}
