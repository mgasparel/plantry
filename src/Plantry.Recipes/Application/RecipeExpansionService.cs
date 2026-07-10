using Plantry.Recipes.Domain;
using Plantry.SharedKernel;

namespace Plantry.Recipes.Application;

/// <summary>
/// The single choke point that resolves a recipe — with its nested inclusions — to a flat, product-level
/// line list with full provenance (recipe-composition.md §4, D4). Every downstream consumer (cook,
/// shortfall, shopping, costing, the diet-tag nudge) reads the expanded view through this one service, so
/// nothing else in the system learns recursion and the single consumption primitive stays product-flat.
///
/// <para>Expansion is a <b>read-time</b> operation — nothing expanded is ever persisted on the Recipe side.
/// For an inclusion of <c>S</c> servings of a sub-recipe with <c>DefaultServings = D</c>, the factor is
/// <c>f = S / D</c>; quantities multiply by the product of factors along the path and are rounded to 3
/// decimal places at the leaf (matching <see cref="Recipe.ChangeDefaultServings"/>). Untracked staples
/// (null quantity/unit) pass through untouched (C12 applies downstream as today).</para>
///
/// <para>The N4 DAG invariant (enforced at save in the application layer) guarantees termination; this
/// service still carries a defensive per-branch ancestor set, so a graph that is cyclic in memory
/// (bypassing N4) terminates with an error rather than recursing forever.</para>
/// </summary>
public sealed class RecipeExpansionService(IRecipeRepository recipes)
{
    /// <summary>
    /// Expands <paramref name="recipeId"/> to its flat <see cref="ExpandedLine"/> list. Returns
    /// <see cref="Error.NotFound"/> when the recipe (or a referenced sub-recipe) cannot be loaded, and a
    /// <c>Recipes.ExpansionCycle</c> error if a cyclic inclusion is encountered (defensive — N4 should
    /// prevent this at save).
    /// </summary>
    public async Task<Result<IReadOnlyList<ExpandedLine>>> ExpandAsync(
        RecipeId recipeId, CancellationToken ct = default)
    {
        var root = await recipes.GetByIdAsync(recipeId, ct);
        if (root is null)
            return Result<IReadOnlyList<ExpandedLine>>.Failure(Error.NotFound);

        var sink = new List<ExpandedLine>();
        // The ancestor set holds only the recipes on the CURRENT DFS branch — a sub legitimately included
        // twice in different branches (D14) is not a cycle, so ids are removed on backtrack.
        var ancestors = new HashSet<RecipeId> { root.Id };
        var error = await ExpandRecipeAsync(root, [], 1m, [], ancestors, sink, ct);
        return error is not null
            ? Result<IReadOnlyList<ExpandedLine>>.Failure(error)
            : Result<IReadOnlyList<ExpandedLine>>.Success(sink);
    }

    /// <summary>
    /// The distinct set of ProductIds across the fully expanded recipe — its own direct ingredients plus every
    /// nested inclusion's ingredients, resolved through <see cref="ExpandAsync"/>'s single recursive repo walk.
    /// This is the cheap cross-aggregate signal the diet-tag nudge guard hashes (recipe-composition.md §8 / D9):
    /// still no LLM and no Catalog name resolution. Propagates the expansion error (missing sub / cycle)
    /// unchanged so callers can decide how to degrade.
    /// </summary>
    public async Task<Result<IReadOnlySet<Guid>>> ExpandedProductIdsAsync(
        RecipeId recipeId, CancellationToken ct = default)
    {
        var expanded = await ExpandAsync(recipeId, ct);
        if (expanded.IsFailure)
            return Result<IReadOnlySet<Guid>>.Failure(expanded.Error);
        IReadOnlySet<Guid> ids = expanded.Value.Select(l => l.ProductId).ToHashSet();
        return Result<IReadOnlySet<Guid>>.Success(ids);
    }

    /// <summary>
    /// Depth-first expansion of one recipe at a given path/factor, appending to <paramref name="sink"/>.
    /// Returns a non-null <see cref="Error"/> to abort (missing sub or cycle); null on success.
    /// </summary>
    private async Task<Error?> ExpandRecipeAsync(
        Recipe recipe,
        IReadOnlyList<InclusionId> path,
        decimal factor,
        IReadOnlyList<string> groupPrefix,
        HashSet<RecipeId> ancestors,
        List<ExpandedLine> sink,
        CancellationToken ct)
    {
        // Emit lines in author order across the union of both line types (N3 shared ordinal space), so a
        // sub-recipe's expanded lines appear at the inclusion's ordinal position within the parent.
        var items = new List<(int Ordinal, Ingredient? Ingredient, Inclusion? Inclusion)>();
        foreach (var ing in recipe.Ingredients)
            items.Add((ing.Ordinal, ing, null));
        foreach (var inc in recipe.Inclusions)
            items.Add((inc.Ordinal, null, inc));
        items.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));

        foreach (var (_, ingredient, inclusion) in items)
        {
            if (ingredient is not null)
            {
                // Untracked staples keep null quantity; tracked lines scale by the accumulated factor and
                // round to 3dp once, at the leaf (source qty × product of factors along the path).
                decimal? quantity = ingredient.Quantity is null
                    ? null
                    : Math.Round(ingredient.Quantity.Value * factor, 3);

                IReadOnlyList<string> groupPath = ingredient.GroupHeading is null
                    ? groupPrefix
                    : [.. groupPrefix, ingredient.GroupHeading];

                sink.Add(new ExpandedLine(
                    path,
                    ingredient.Id,
                    recipe.Id,
                    ingredient.ProductId,
                    quantity,
                    ingredient.UnitId,
                    groupPath));
                continue;
            }

            var inc = inclusion!;

            // Defensive cycle guard (N4 should prevent this at save). A sub already on the current branch
            // would recurse forever — abort with an error instead of hanging.
            if (ancestors.Contains(inc.SubRecipeId))
                return Error.Custom("Recipes.ExpansionCycle",
                    $"Cyclic inclusion detected: recipe {inc.SubRecipeId} includes itself transitively.");

            var sub = await recipes.GetByIdAsync(inc.SubRecipeId, ct);
            if (sub is null)
                return Error.Custom("Recipes.ExpansionSubNotFound",
                    $"Included recipe {inc.SubRecipeId} could not be loaded.");

            // f = S / D; multiply along the path. DefaultServings is R2-guaranteed ≥ 1, so no divide-by-zero.
            var subFactor = factor * (inc.Servings / sub.DefaultServings);
            var subPath = new List<InclusionId>(path) { inc.Id };
            var subGroupPrefix = new List<string>(groupPrefix) { sub.Name };

            ancestors.Add(inc.SubRecipeId);
            var error = await ExpandRecipeAsync(sub, subPath, subFactor, subGroupPrefix, ancestors, sink, ct);
            ancestors.Remove(inc.SubRecipeId);
            if (error is not null)
                return error;
        }

        return null;
    }
}

/// <summary>
/// One product-level line of an expanded recipe (recipe-composition.md §4). The pair
/// (<see cref="Path"/>, <see cref="IngredientId"/>) is the unique identity of a line (D6) — the sub's
/// <see cref="IngredientId"/> alone is not unique once a sub can appear more than once in a tree (D14).
/// </summary>
/// <param name="Path">
/// The chain of <see cref="InclusionId"/>s from the root recipe down to the line's owning recipe; empty
/// for the root recipe's own direct ingredients. Serialize with <see cref="PathKey"/> for form fields.
/// </param>
/// <param name="IngredientId">The ingredient's id in ITS OWN recipe (re-minted per save, O1).</param>
/// <param name="SourceRecipeId">The recipe the ingredient physically belongs to (cook-history provenance, D8).</param>
/// <param name="ProductId">Soft ref → catalog.product (DM-3).</param>
/// <param name="Quantity">
/// Source quantity × product of factors along <see cref="Path"/>, rounded to 3dp; null stays null
/// (untracked staple, C12).
/// </param>
/// <param name="UnitId">Soft ref → catalog.unit (DM-3); null for an untracked staple.</param>
/// <param name="GroupPath">
/// Inclusion display names down the path plus the source line's own GroupHeading, for rendering (§4/§6).
/// </param>
public sealed record ExpandedLine(
    IReadOnlyList<InclusionId> Path,
    IngredientId IngredientId,
    RecipeId SourceRecipeId,
    Guid ProductId,
    decimal? Quantity,
    Guid? UnitId,
    IReadOnlyList<string> GroupPath)
{
    /// <summary>
    /// The '/'-joined GUID serialization of <see cref="Path"/> for form fields — an EMPTY string for a
    /// direct ingredient, preserving the shape existing single-key call sites already produce (§4).
    /// </summary>
    public string PathKey => Path.Count == 0
        ? string.Empty
        : string.Join('/', Path.Select(p => p.Value));
}
