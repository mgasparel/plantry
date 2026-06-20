using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Plantry.Migration.Grocy.Dto;

namespace Plantry.Migration.Grocy;

/// <summary>
/// Stages all Grocy normal recipes from the manifest into <see cref="RecipeStagingRow"/> records.
///
/// Algorithm (per grocy-import-plan.md §5.1–§5.4):
///
/// 1. <b>Directions HTML→text conversion</b> (§5.1):
///    - Each &lt;p&gt; block → one step followed by a blank line.
///    - &lt;li&gt; items → individual lines, each followed by a blank line (separate steps).
///    - &lt;br&gt; within a block → line break within the same step (no blank line).
///    - Strip all remaining HTML tags; decode HTML entities.
///    - Collapse runs of 3+ newlines to 2.
///    - Post-conversion normalisation: enforce blank-line-between-steps.
///
/// 2. <b>Nesting flatten</b> (16 edges on normal recipes, §5.3):
///    - For each nesting edge, scale the sub-recipe's ingredients by
///      (nesting.amount / sub_recipe.base_servings) and insert them into the
///      parent recipe under a group_heading named after the sub-recipe.
///
/// 3. <b>Ingredient staging</b> (§5.4):
///    - Resolve product_id via the product crosswalk.
///    - Resolve qu_id via the unit crosswalk.
///    - Remap ingredient_group → group_heading.
///    - Drop ingredient notes (8 total) — set HasDroppedNotes flag.
///
/// 4. <b>Photo staging</b> (§5.1): photo bytes from the manifest RecipePhotos collection.
///
/// 5. <b>Source mapping</b>: userfields.original_recipe → source.
///    When recipe.product_id is set, a provenance line "produces: ProductName" is appended (§5.2).
///
/// 6. <b>Name collision detection</b> against existing recipe names.
///    Collisions get a disambiguating " (Grocy)" suffix.
/// </summary>
public static class RecipeStager
{
    // Regex patterns for HTML conversion (compiled for performance across 65 recipes)

    // Match <p ...> ... </p> blocks — non-greedy, DOTALL
    private static readonly Regex ParagraphRegex =
        new(@"<p(?:\s[^>]*)?>(?<inner>.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    // Match <li ...> ... </li> blocks — non-greedy, DOTALL
    private static readonly Regex ListItemRegex =
        new(@"<li(?:\s[^>]*)?>(?<inner>.*?)</li>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    // Match <br> and <br /> and <BR> etc
    private static readonly Regex BreakRegex =
        new(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Match any remaining HTML tag
    private static readonly Regex AnyTagRegex =
        new(@"<[^>]+>", RegexOptions.Compiled);

    // Three or more consecutive newlines → exactly two (one blank line)
    private static readonly Regex MultipleNewlinesRegex =
        new(@"\n{3,}", RegexOptions.Compiled);

    /// <summary>
    /// Stages all recipes from the manifest and returns staging rows in Grocy-id order.
    /// </summary>
    /// <param name="manifest">The Grocy manifest (recipes, positions, nestings, photos, userfields).</param>
    /// <param name="productCrosswalk">
    /// grocy_product_id → plantry_product_id map. Pass null to treat all product lookups as missing.
    /// </param>
    /// <param name="productIdToName">
    /// grocy_product_id → product name for display. Pass null to omit product names.
    /// </param>
    /// <param name="unitCrosswalk">
    /// grocy_unit_id → plantry_unit_id map. Pass null to treat all unit lookups as missing.
    /// </param>
    /// <param name="unitIdToName">
    /// grocy_unit_id → unit name for display. Pass null to omit unit names.
    /// </param>
    /// <param name="existingRecipeNames">
    /// Recipe names already in the household (case-insensitive). Used to detect NameCollision.
    /// Pass null or empty when no recipes exist yet.
    /// </param>
    public static IReadOnlyList<RecipeStagingRow> Stage(
        GrocyManifest manifest,
        IReadOnlyDictionary<int, Guid>? productCrosswalk,
        IReadOnlyDictionary<int, string>? productIdToName,
        IReadOnlyDictionary<int, Guid>? unitCrosswalk,
        IReadOnlyDictionary<int, string>? unitIdToName,
        IReadOnlySet<string>? existingRecipeNames = null)
    {
        // Build lookup tables from the manifest
        var recipeById = manifest.Recipes.ToDictionary(r => r.Id);
        var positionsByRecipeId = manifest.RecipePositions
            .GroupBy(p => p.RecipeId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var nestingsByRecipeId = manifest.RecipeNestings
            .GroupBy(n => n.RecipeId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var photoByRecipeId = manifest.RecipePhotos
            .ToDictionary(p => p.RecipeId);
        var userfieldByRecipeId = manifest.RecipeUserfields
            .ToDictionary(u => u.RecipeId);

        var existingNames = existingRecipeNames
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Track names assigned so far in this run (intra-batch collision detection)
        var assignedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var rows = new List<RecipeStagingRow>(manifest.Recipes.Count);

        foreach (var recipe in manifest.Recipes.OrderBy(r => r.Id))
        {
            var row = new RecipeStagingRow
            {
                GrocyId        = recipe.Id,
                GrocyName      = recipe.Name,
                BaseServings   = recipe.BaseServings,
                GrocyProductId = recipe.ProductId,
                PictureFileName = recipe.PictureFileName,
                CreatedAt      = ParseTimestamp(recipe.RowCreatedTimestamp),
            };

            // ── 1. Name collision detection ──────────────────────────────────
            var candidateName = recipe.Name.Trim();
            if (existingNames.Contains(candidateName) || assignedNames.Contains(candidateName))
            {
                row.Flags |= RecipeStagingFlags.NameCollision;
                candidateName = $"{candidateName} (Grocy)";
            }
            row.PlantryName = candidateName;
            assignedNames.Add(candidateName);

            // ── 2. Directions HTML→text conversion ───────────────────────────
            row.Directions = ConvertDirectionsHtmlToText(recipe.Description);

            // ── 3. Source (userfields.original_recipe) ───────────────────────
            var source = userfieldByRecipeId.TryGetValue(recipe.Id, out var uf)
                ? uf.OriginalRecipe
                : null;

            // Append "produces: ProductName" provenance when product_id is set (§5.2 / §8-T12)
            if (recipe.ProductId is { } producesProductId)
            {
                row.Flags |= RecipeStagingFlags.HasProducesProduct;
                var productName = productIdToName is not null
                    && productIdToName.TryGetValue(producesProductId, out var pn)
                    ? pn
                    : $"product #{producesProductId}";

                var provenance = $"produces: {productName}";
                source = string.IsNullOrWhiteSpace(source)
                    ? provenance
                    : $"{source.TrimEnd()} · {provenance}";
            }
            row.Source = source;

            // ── 4. Photo ──────────────────────────────────────────────────────
            if (photoByRecipeId.TryGetValue(recipe.Id, out var photo))
            {
                row.PhotoBytes = photo.Bytes;
                row.PhotoContentType = photo.ContentType;
            }

            // ── 5. Ingredients (direct positions + flattened nestings) ────────
            var directPositions = positionsByRecipeId.TryGetValue(recipe.Id, out var pos)
                ? pos
                : [];

            var nestings = nestingsByRecipeId.TryGetValue(recipe.Id, out var nest)
                ? nest
                : [];

            var (ingredients, flattenedNames, hasDroppedNotes, hasCrosswalkMissing) =
                BuildIngredients(
                    directPositions,
                    nestings,
                    recipeById,
                    positionsByRecipeId,
                    productCrosswalk,
                    productIdToName,
                    unitCrosswalk,
                    unitIdToName);

            row.Ingredients = ingredients;
            row.FlattenedSubRecipeNames = flattenedNames;

            if (flattenedNames.Count > 0)
                row.Flags |= RecipeStagingFlags.HasFlattenedNesting;
            if (hasDroppedNotes)
                row.Flags |= RecipeStagingFlags.HasDroppedNotes;
            if (hasCrosswalkMissing)
                row.Flags |= RecipeStagingFlags.CrosswalkMissing;

            rows.Add(row);
        }

        return rows;
    }

    // ──────────── Directions HTML → text conversion ─────────────────────────

    /// <summary>
    /// Converts Grocy HTML directions to Plantry plain-text directions.
    ///
    /// Rules (plan §5.1):
    /// - &lt;p&gt; → one step + blank line
    /// - &lt;li&gt; → one step + blank line
    /// - &lt;br&gt; → line break within the same step (no blank line)
    /// - Strip remaining tags; decode HTML entities
    /// - Collapse 3+ newlines → 2
    /// - Post-conversion: ensure every non-empty step is followed by a blank line
    /// </summary>
    public static string? ConvertDirectionsHtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var sb = new StringBuilder(html.Length);

        // Step 1: Replace <li>...</li> → "inner\n\n" (each list item = one step)
        var withLi = ListItemRegex.Replace(html, m =>
        {
            var inner = m.Groups["inner"].Value;
            return inner + "\n\n";
        });

        // Step 2: Replace <p>...</p> → "inner\n\n" (each paragraph = one step)
        var withP = ParagraphRegex.Replace(withLi, m =>
        {
            var inner = m.Groups["inner"].Value;
            return inner + "\n\n";
        });

        // Step 3: Replace <br> → "\n" (line break within a step, no blank line)
        var withBr = BreakRegex.Replace(withP, "\n");

        // Step 4: Strip remaining HTML tags
        var stripped = AnyTagRegex.Replace(withBr, string.Empty);

        // Step 5: Decode HTML entities
        var decoded = System.Net.WebUtility.HtmlDecode(stripped);

        // Step 6: Normalize line endings to \n
        decoded = decoded.Replace("\r\n", "\n").Replace("\r", "\n");

        // Step 7: Collapse 3+ consecutive newlines to 2
        var collapsed = MultipleNewlinesRegex.Replace(decoded, "\n\n");

        // Step 8: Post-conversion normalisation — split into steps, ensure blank line between each.
        // A "step" is a non-empty block of text (separated by blank lines).
        var steps = collapsed
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        if (steps.Count == 0)
            return null;

        // Rejoin with blank lines between each step (the invariant Plantry expects)
        return string.Join("\n\n", steps);
    }

    // ──────────── Ingredient building with nesting flatten ──────────────────

    private static (
        IReadOnlyList<StagedIngredient> Ingredients,
        IReadOnlyList<string> FlattenedSubRecipeNames,
        bool HasDroppedNotes,
        bool HasCrosswalkMissing
    ) BuildIngredients(
        IReadOnlyList<GrocyRecipePosition> directPositions,
        IReadOnlyList<GrocyRecipeNesting> nestings,
        IReadOnlyDictionary<int, GrocyRecipe> recipeById,
        IReadOnlyDictionary<int, List<GrocyRecipePosition>> positionsByRecipeId,
        IReadOnlyDictionary<int, Guid>? productCrosswalk,
        IReadOnlyDictionary<int, string>? productIdToName,
        IReadOnlyDictionary<int, Guid>? unitCrosswalk,
        IReadOnlyDictionary<int, string>? unitIdToName)
    {
        var result = new List<StagedIngredient>();
        var flattenedSubRecipeNames = new List<string>();
        var hasDroppedNotes = false;
        var hasCrosswalkMissing = false;
        var ordinal = 0;

        // 1. Direct positions (sorted by their Grocy id for stable ordering)
        foreach (var pos in directPositions.OrderBy(p => p.Id))
        {
            var ing = ResolveIngredient(
                pos,
                amount: pos.Amount,
                overrideGroupHeading: null,
                isFromNesting: false,
                ordinal: ordinal++,
                productCrosswalk,
                productIdToName,
                unitCrosswalk,
                unitIdToName);

            if (!string.IsNullOrWhiteSpace(pos.Note))
                hasDroppedNotes = true;

            if (ing.PlantryProductId is null || ing.PlantryUnitId is null)
                hasCrosswalkMissing = true;

            result.Add(ing);
        }

        // 2. Flattened nestings — insert scaled sub-recipe ingredients (§5.3)
        foreach (var nesting in nestings.OrderBy(n => n.Id))
        {
            if (!recipeById.TryGetValue(nesting.IncludesRecipeId, out var subRecipe))
                continue; // Sub-recipe not in manifest — skip silently

            var subPositions = positionsByRecipeId.TryGetValue(subRecipe.Id, out var sp)
                ? sp
                : [];

            if (subPositions.Count == 0)
                continue;

            // Scale factor: nesting.amount is how many "servings" of the sub-recipe are used.
            // base_servings is the sub-recipe's canonical serving count.
            // factor = nesting.amount / sub_recipe.base_servings
            var scaleFactor = subRecipe.BaseServings > 0
                ? nesting.Servings / subRecipe.BaseServings
                : 1m;

            // Sub-recipe name becomes the group_heading for all its flattened ingredients
            var subRecipeName = subRecipe.Name;
            flattenedSubRecipeNames.Add(subRecipeName);

            foreach (var pos in subPositions.OrderBy(p => p.Id))
            {
                var scaledAmount = pos.Amount * scaleFactor;

                var ing = ResolveIngredient(
                    pos,
                    amount: scaledAmount,
                    overrideGroupHeading: subRecipeName,
                    isFromNesting: true,
                    ordinal: ordinal++,
                    productCrosswalk,
                    productIdToName,
                    unitCrosswalk,
                    unitIdToName);

                // Notes on nesting positions are also dropped (same policy as direct)
                if (!string.IsNullOrWhiteSpace(pos.Note))
                    hasDroppedNotes = true;

                if (ing.PlantryProductId is null || ing.PlantryUnitId is null)
                    hasCrosswalkMissing = true;

                result.Add(ing);
            }
        }

        return (result, flattenedSubRecipeNames, hasDroppedNotes, hasCrosswalkMissing);
    }

    private static StagedIngredient ResolveIngredient(
        GrocyRecipePosition pos,
        decimal amount,
        string? overrideGroupHeading,
        bool isFromNesting,
        int ordinal,
        IReadOnlyDictionary<int, Guid>? productCrosswalk,
        IReadOnlyDictionary<int, string>? productIdToName,
        IReadOnlyDictionary<int, Guid>? unitCrosswalk,
        IReadOnlyDictionary<int, string>? unitIdToName)
    {
        var ing = new StagedIngredient
        {
            GrocyPositionId  = pos.Id,
            GrocyProductId   = pos.ProductId,
            GrocyUnitId      = pos.QuId,
            Amount           = amount,
            IngredientGroup  = pos.IngredientGroup,
            DroppedNote      = string.IsNullOrWhiteSpace(pos.Note) ? null : pos.Note,
            GroupHeading     = overrideGroupHeading ?? pos.IngredientGroup,
            Ordinal          = ordinal,
            IsFromNesting    = isFromNesting,
        };

        // Resolve product
        if (productCrosswalk is not null && productCrosswalk.TryGetValue(pos.ProductId, out var plantryProductId))
            ing.PlantryProductId = plantryProductId;

        if (productIdToName is not null && productIdToName.TryGetValue(pos.ProductId, out var productName))
            ing.ProductName = productName;

        // Resolve unit
        if (unitCrosswalk is not null && unitCrosswalk.TryGetValue(pos.QuId, out var plantryUnitId))
            ing.PlantryUnitId = plantryUnitId;

        if (unitIdToName is not null && unitIdToName.TryGetValue(pos.QuId, out var unitName))
            ing.UnitName = unitName;

        return ing;
    }

    // ──────────── Helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Parses Grocy's "YYYY-MM-DD HH:MM:SS" timestamp format. Returns null on failure.
    /// </summary>
    public static DateTimeOffset? ParseTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (DateTimeOffset.TryParseExact(raw, "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            return dto;
        }

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto2))
            return dto2;

        return null;
    }

    /// <summary>
    /// Marks <see cref="RecipeStagingRow.IsDropped"/> on every row whose
    /// <see cref="RecipeStagingRow.GrocyId"/> appears in <paramref name="droppedIds"/>.
    ///
    /// <para>
    /// Called by the /Import/Recipes page model to reconcile the two sources of drop state:
    /// <list type="bullet">
    ///   <item>Current-page selections (Alpine-driven hidden inputs, submitted on POST).</item>
    ///   <item>Cross-page selections (carried as <c>droppedIds</c> query-string parameters on GET,
    ///         or as extra hidden form inputs on POST).</item>
    /// </list>
    /// Merging is done at the call site; this method simply stamps the rows given a unified set.
    /// </para>
    ///
    /// <para>Idempotent — calling it multiple times with the same set is safe.</para>
    /// </summary>
    /// <param name="rows">Staging rows produced by <see cref="Stage"/>.</param>
    /// <param name="droppedIds">Unified set of Grocy recipe IDs to mark as dropped.</param>
    public static void ApplyDrops(IReadOnlyList<RecipeStagingRow> rows, IEnumerable<int> droppedIds)
    {
        var droppedSet = new HashSet<int>(droppedIds);
        if (droppedSet.Count == 0)
            return;

        foreach (var row in rows)
        {
            if (droppedSet.Contains(row.GrocyId))
                row.IsDropped = true;
        }
    }
}
