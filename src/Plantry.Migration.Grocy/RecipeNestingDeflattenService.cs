using Plantry.Migration.Grocy.Dto;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Migration.Grocy;

/// <summary>
/// Re-imports the Grocy nesting edges as recipe <b>inclusions</b>, retroactively redeeming the T14
/// flatten tradeoff (grocy-import-plan.md §5.3/§8-T14, recipe-composition.md §10/D16).
///
/// <para>
/// At recipe commit (<see cref="RecipeCommitService"/>) each nesting edge was <em>flattened</em>: the
/// sub-recipe's ingredients were scaled and inlined into the parent under a group heading named after the
/// sub. Now that the Recipe aggregate supports native inclusions (plantry-fqb0.1), those edges can become
/// first-class <see cref="Inclusion"/> lines — Grocy's nesting <c>amount</c> is already denominated in
/// servings of the sub (D2, a direct copy).
/// </para>
///
/// <para>
/// <b>De-flatten rule (decided — no design fork).</b> For each parent that has nesting edges and whose
/// parent <em>and</em> every sub committed:
/// <list type="number">
///   <item>Recompute the FLATTENED staging output from the manifest (<see cref="RecipeStager"/> already
///         produces it) and take its committable <c>(ProductId, Quantity, UnitId, GroupHeading)</c> multiset —
///         the subset with a resolved product and unit, exactly what <see cref="RecipeCommitService"/>
///         committed (crosswalk-missing lines were skipped at commit).</item>
///   <item>Compare that multiset to the parent's CURRENT committed lines.
///         <b>Equal</b> (untouched since import) → wholesale-replace the parent's line set with the
///         non-flattened projection: the parent's DIRECT ingredients plus one <see cref="Inclusion"/> line
///         per edge (servings = the Grocy nesting amount). <b>Not equal</b> (the user edited since import) →
///         SKIP and report; never merge into an edited recipe.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Idempotent by construction.</b> After a successful de-flatten the parent already carries the
/// inclusion lines, so a re-run detects the expected sub set already present and reports
/// <see cref="DeflattenDisposition.AlreadyConverted"/> — it never re-mutates or duplicates inclusions.
/// </para>
/// </summary>
public sealed class RecipeNestingDeflattenService(
    AuthorRecipe authorRecipe,
    IRecipeRepository recipes,
    ITenantContext tenant)
{
    // ──────────── Result types ──────────────────────────────────────────────

    public enum DeflattenDisposition
    {
        /// <summary>Untouched-since-import parent — flattened lines replaced by direct ingredients + inclusions.</summary>
        Converted,

        /// <summary>Parent already carries the expected inclusion lines (a prior de-flatten). No mutation.</summary>
        AlreadyConverted,

        /// <summary>Parent's current lines differ from the recomputed flatten output — user edited since import. Skipped, no mutation.</summary>
        SkippedEdited,

        /// <summary>At least one nesting edge points at a sub-recipe that was not committed (missing crosswalk entry). Skipped.</summary>
        SkippedMissingSub,

        /// <summary>The parent itself was not committed (dropped, or all its ingredients were crosswalk-missing). Skipped.</summary>
        SkippedParentNotCommitted,

        /// <summary>Conversion was attempted but <see cref="AuthorRecipe"/> rejected the edit. No mutation persisted.</summary>
        Failed,
    }

    /// <summary>Per-parent outcome of the de-flatten pass.</summary>
    public sealed record ParentDeflattenResult(
        int ParentGrocyId,
        string ParentName,
        Guid? ParentPlantryId,
        DeflattenDisposition Disposition,
        IReadOnlyList<string> SubRecipeNames,
        string? Note);

    /// <summary>Aggregated de-flatten result rendered on the /Import/Recipes page after the action runs.</summary>
    public sealed record DeflattenSummary(IReadOnlyList<ParentDeflattenResult> Results)
    {
        /// <summary>Parents with at least one nesting edge — the candidate set the pass considered.</summary>
        public int ParentsWithNestings => Results.Count;

        public int Converted                 => Count(DeflattenDisposition.Converted);
        public int AlreadyConverted          => Count(DeflattenDisposition.AlreadyConverted);
        public int SkippedEdited             => Count(DeflattenDisposition.SkippedEdited);
        public int SkippedMissingSub         => Count(DeflattenDisposition.SkippedMissingSub);
        public int SkippedParentNotCommitted => Count(DeflattenDisposition.SkippedParentNotCommitted);
        public int Failed                    => Count(DeflattenDisposition.Failed);

        /// <summary>Every disposition other than <see cref="DeflattenDisposition.Converted"/>.</summary>
        public int Skipped => ParentsWithNestings - Converted;

        private int Count(DeflattenDisposition d) => Results.Count(r => r.Disposition == d);
    }

    // ──────────── Main entry point ──────────────────────────────────────────

    /// <summary>
    /// Runs the de-flatten pass over every parent recipe that has nesting edges in the manifest.
    /// Reads the recipe crosswalk sidecar (written by <see cref="RecipeCommitService"/>) to resolve Grocy
    /// recipe ids to committed Plantry recipe ids. No-op for a parent whose flattened lines were edited
    /// since import (reported, never mutated).
    /// </summary>
    /// <param name="stagingRows">Staging rows from <see cref="RecipeStager.Stage"/> (resolved product/unit ids).</param>
    /// <param name="manifest">The Grocy manifest — source of the nesting edges and sub-recipe names.</param>
    /// <param name="manifestFilePath">Manifest path — used to resolve the recipe-crosswalk.json sidecar.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<DeflattenSummary> DeflattenAsync(
        IReadOnlyList<RecipeStagingRow> stagingRows,
        GrocyManifest manifest,
        string manifestFilePath,
        CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            throw new InvalidOperationException("No household in tenant context — cannot de-flatten nestings.");

        // Grocy recipe id → committed Plantry recipe id (null = intentionally dropped / not committed).
        var crosswalkPath = RecipeCrosswalk.ResolvePath(manifestFilePath);
        var crosswalk = await RecipeCrosswalk.TryReadAsync(crosswalkPath, ct);
        var mappings = crosswalk?.Mappings ?? new Dictionary<string, Guid?>();

        // Nesting edges grouped by parent Grocy recipe id (edges only exist for in-scope normal recipes).
        var nestingsByParent = manifest.RecipeNestings
            .GroupBy(n => n.RecipeId)
            .ToDictionary(g => g.Key, g => g.OrderBy(n => n.Id).ToList());

        var recipeNameByGrocyId = manifest.Recipes.ToDictionary(r => r.Id, r => r.Name);
        var stagingByGrocyId    = stagingRows.ToDictionary(r => r.GrocyId);

        var results = new List<ParentDeflattenResult>(nestingsByParent.Count);

        // Deterministic order (ascending parent Grocy id) so the summary is stable across runs.
        foreach (var (parentGrocyId, edges) in nestingsByParent.OrderBy(kv => kv.Key))
        {
            var result = await DeflattenParentAsync(
                parentGrocyId, edges, mappings, recipeNameByGrocyId, stagingByGrocyId, ct);
            results.Add(result);
        }

        return new DeflattenSummary(results);
    }

    // ──────────── Per-parent de-flatten ─────────────────────────────────────

    private async Task<ParentDeflattenResult> DeflattenParentAsync(
        int parentGrocyId,
        IReadOnlyList<GrocyRecipeNesting> edges,
        IReadOnlyDictionary<string, Guid?> mappings,
        IReadOnlyDictionary<int, string> recipeNameByGrocyId,
        IReadOnlyDictionary<int, RecipeStagingRow> stagingByGrocyId,
        CancellationToken ct)
    {
        var parentName = recipeNameByGrocyId.GetValueOrDefault(parentGrocyId, $"recipe #{parentGrocyId}");
        var subNames = edges
            .Select(e => recipeNameByGrocyId.GetValueOrDefault(e.IncludesRecipeId, $"recipe #{e.IncludesRecipeId}"))
            .ToList();

        ParentDeflattenResult Result(DeflattenDisposition disposition, Guid? parentId, string? note) =>
            new(parentGrocyId, parentName, parentId, disposition, subNames, note);

        // ── Parent must be committed ─────────────────────────────────────────
        if (!TryResolveCommitted(mappings, parentGrocyId, out var parentPlantryId))
            return Result(DeflattenDisposition.SkippedParentNotCommitted, null,
                "Parent recipe was not committed (dropped or all ingredients crosswalk-missing).");

        // ── Every sub must be committed ──────────────────────────────────────
        var subPlantryIds = new List<Guid>(edges.Count);
        var missingSubs = new List<string>();
        foreach (var edge in edges)
        {
            if (TryResolveCommitted(mappings, edge.IncludesRecipeId, out var subId))
                subPlantryIds.Add(subId);
            else
                missingSubs.Add(recipeNameByGrocyId.GetValueOrDefault(
                    edge.IncludesRecipeId, $"recipe #{edge.IncludesRecipeId}"));
        }

        if (missingSubs.Count > 0)
            return Result(DeflattenDisposition.SkippedMissingSub, parentPlantryId,
                $"Uncommitted/missing sub-recipe(s): {string.Join(", ", missingSubs)}.");

        // ── Load the committed parent aggregate ──────────────────────────────
        var parent = await recipes.GetByIdAsync(RecipeId.From(parentPlantryId), ct);
        if (parent is null)
            return Result(DeflattenDisposition.SkippedParentNotCommitted, parentPlantryId,
                "Parent recipe id is in the crosswalk but no longer resolves in this household.");

        // ── Idempotency: already carries the expected inclusions? ────────────
        var expectedSubIds = subPlantryIds.Select(RecipeId.From).ToHashSet();
        var presentSubIds = parent.Inclusions.Select(i => i.SubRecipeId).ToHashSet();
        if (expectedSubIds.All(presentSubIds.Contains) && expectedSubIds.Count > 0)
            return Result(DeflattenDisposition.AlreadyConverted, parentPlantryId,
                "Parent already includes the sub-recipe(s) as inclusion lines.");

        // ── Untouched-since-import check: committable flatten multiset == current lines ──
        if (!stagingByGrocyId.TryGetValue(parentGrocyId, out var stagingRow))
            return Result(DeflattenDisposition.SkippedParentNotCommitted, parentPlantryId,
                "No staging row for the parent recipe (manifest/staging mismatch).");

        var flattenMultiset = CommittableFlattenLines(stagingRow.Ingredients);
        var currentMultiset = parent.Ingredients.Select(LineKey);

        if (!SameMultiset(flattenMultiset, currentMultiset))
            return Result(DeflattenDisposition.SkippedEdited, parentPlantryId,
                "Parent's current lines differ from the recomputed flatten output — edited since import; not converting.");

        // ── Convert: direct ingredients + one inclusion per edge (servings = nesting amount) ──
        return await ConvertParentAsync(parent, stagingRow, edges, subPlantryIds, subNames, ct);
    }

    private async Task<ParentDeflattenResult> ConvertParentAsync(
        Recipe parent,
        RecipeStagingRow stagingRow,
        IReadOnlyList<GrocyRecipeNesting> edges,
        IReadOnlyList<Guid> subPlantryIds,
        IReadOnlyList<string> subNames,
        CancellationToken ct)
    {
        // Direct (non-nesting) committable ingredients keep their staged order; inclusions follow.
        var directLines = stagingRow.Ingredients
            .Where(i => !i.IsFromNesting && i.PlantryProductId is not null && i.PlantryUnitId is not null)
            .OrderBy(i => i.Ordinal)
            .Select((i, idx) => new AuthorIngredientLine(
                ProductId:    i.PlantryProductId,
                Quantity:     i.Amount,
                UnitId:       i.PlantryUnitId,
                GroupHeading: i.GroupHeading,
                Ordinal:      idx))
            .ToList();

        // One inclusion per edge — servings = the Grocy nesting amount (D2, direct copy). The inclusion line
        // carries no group heading: it inherently references the sub-recipe (which has its own name), so the
        // flatten's sub-name section heading is redundant once the whole section becomes a single line.
        var inclusionLines = edges
            .Select((edge, idx) => new AuthorInclusionLine(
                SubRecipeId:  subPlantryIds[idx],
                Servings:     edge.Servings,
                GroupHeading: null,
                Ordinal:      directLines.Count + idx))
            .ToList();

        // Re-author with the SAME scalars (source/directions/cook-time/servings/tags) so nothing else changes —
        // AuthorRecipe re-applies them wholesale on edit. ScaleMode.Keep leaves quantities untouched.
        var command = new AuthorRecipeCommand(
            RecipeId:        parent.Id,
            Name:            parent.Name,
            DefaultServings: parent.DefaultServings,
            Lines:           directLines,
            TagIds:          parent.Tags.Select(t => t.TagId.Value).ToList(),
            Source:          parent.Source,
            CookTimeMinutes: parent.CookTimeMinutes,
            Directions:      parent.Directions,
            ScaleMode:       ScaleMode.Keep,
            Inclusions:      inclusionLines);

        var authorResult = await authorRecipe.ExecuteAsync(command, ct);

        return authorResult switch
        {
            AuthorRecipeResult.Saved =>
                new ParentDeflattenResult(stagingRow.GrocyId, parent.Name, parent.Id.Value,
                    DeflattenDisposition.Converted, subNames,
                    $"Converted {edges.Count} nesting edge(s) to inclusion(s); " +
                    $"{directLines.Count} direct ingredient(s) retained."),

            AuthorRecipeResult.Invalid invalid =>
                new ParentDeflattenResult(stagingRow.GrocyId, parent.Name, parent.Id.Value,
                    DeflattenDisposition.Failed, subNames,
                    $"AuthorRecipe rejected the de-flatten: {invalid.Error.Description}"),

            AuthorRecipeResult.NeedsConversion needs =>
                new ParentDeflattenResult(stagingRow.GrocyId, parent.Name, parent.Id.Value,
                    DeflattenDisposition.Failed, subNames,
                    $"AuthorRecipe requires {needs.Conversions.Count} unit conversion(s) for the retained direct " +
                    "ingredients — resolve them before de-flattening."),

            _ =>
                new ParentDeflattenResult(stagingRow.GrocyId, parent.Name, parent.Id.Value,
                    DeflattenDisposition.Failed, subNames, "Unexpected AuthorRecipe result type."),
        };
    }

    // ──────────── Helpers ───────────────────────────────────────────────────

    /// <summary>
    /// True when <paramref name="grocyId"/> has a non-null Plantry id in the crosswalk (committed).
    /// A missing key or a null value (intentionally dropped / not committed) yields false.
    /// </summary>
    private static bool TryResolveCommitted(
        IReadOnlyDictionary<string, Guid?> mappings, int grocyId, out Guid plantryId)
    {
        if (mappings.TryGetValue(grocyId.ToString(), out var mapped) && mapped is { } id)
        {
            plantryId = id;
            return true;
        }
        plantryId = default;
        return false;
    }

    /// <summary>
    /// The committable flatten projection: the staged ingredients (direct + flattened) that had a resolved
    /// product AND unit — exactly the subset <see cref="RecipeCommitService"/> committed. Crosswalk-missing
    /// lines were skipped at commit, so they must be excluded here for an apples-to-apples comparison.
    /// </summary>
    private static IEnumerable<LineTuple> CommittableFlattenLines(IReadOnlyList<StagedIngredient> staged) =>
        staged
            .Where(i => i.PlantryProductId is not null && i.PlantryUnitId is not null)
            .Select(i => new LineTuple(i.PlantryProductId!.Value, i.Amount, i.PlantryUnitId, i.GroupHeading));

    private static LineTuple LineKey(Ingredient ing) =>
        new(ing.ProductId, ing.Quantity, ing.UnitId, ing.GroupHeading);

    /// <summary>Order-independent multiset equality (bag comparison) of the line tuples.</summary>
    private static bool SameMultiset(IEnumerable<LineTuple> a, IEnumerable<LineTuple> b)
    {
        var countsA = new Dictionary<LineTuple, int>();
        foreach (var t in a)
            countsA[t] = countsA.GetValueOrDefault(t) + 1;

        var countsB = new Dictionary<LineTuple, int>();
        foreach (var t in b)
            countsB[t] = countsB.GetValueOrDefault(t) + 1;

        if (countsA.Count != countsB.Count)
            return false;

        foreach (var (key, count) in countsA)
            if (!countsB.TryGetValue(key, out var other) || other != count)
                return false;

        return true;
    }

    /// <summary>
    /// Comparison key for one recipe line. Quantity/UnitId are nullable so a hand-added untracked-staple line
    /// (null qty/unit — Grocy never produces one) simply never matches a flatten tuple (which always has both),
    /// yielding the safe "edited" verdict. Decimal equality is scale-insensitive, so <c>2</c> and <c>2.0</c> match.
    /// </summary>
    private readonly record struct LineTuple(Guid ProductId, decimal? Quantity, Guid? UnitId, string? GroupHeading);
}
