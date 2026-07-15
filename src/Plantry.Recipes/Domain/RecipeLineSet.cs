using Plantry.SharedKernel;

namespace Plantry.Recipes.Domain;

/// <summary>
/// Validated value object holding a recipe's complete authored line set — its <see cref="IngredientLine"/>s
/// and included sub-recipe <see cref="InclusionLine"/>s — after every line-set invariant has been checked
/// (recipe-composition.md §3, D3). Construct through <see cref="Create"/>, the one place the union invariants
/// are enforced:
/// <list type="bullet">
/// <item>R3′ — at least one ingredient OR inclusion is required (D3).</item>
/// <item>R4 — every ingredient ProductId must be non-empty.</item>
/// <item>R5 — ingredient Quantity and UnitId must both be set or both null.</item>
/// <item>N1 — every inclusion Servings must be &gt; 0.</item>
/// <item>N2 — no inclusion may reference the owning recipe (no self-inclusion).</item>
/// <item>N3 (R6 widened) — ordinals must be contiguous across the UNION of ingredient and inclusion lines.</item>
/// </list>
/// A successfully created instance is guaranteed valid, so <see cref="Recipe.ReplaceLines"/> applies it with
/// no re-validation. The cross-aggregate DAG / same-household / sub-existence checks (N4) are enforced by the
/// application layer before these lines are assembled and are out of scope here. This is a transient
/// construction/validation vehicle only — it is never persisted (EF maps the aggregate's Ingredient/Inclusion
/// entities, not this type).
/// </summary>
public sealed class RecipeLineSet
{
    /// <summary>The validated ingredient lines, in author order.</summary>
    public IReadOnlyList<IngredientLine> Ingredients { get; }

    /// <summary>The validated inclusion (included sub-recipe) lines, in author order.</summary>
    public IReadOnlyList<InclusionLine> Inclusions { get; }

    private RecipeLineSet(IReadOnlyList<IngredientLine> ingredients, IReadOnlyList<InclusionLine> inclusions)
    {
        Ingredients = ingredients;
        Inclusions = inclusions;
    }

    /// <summary>
    /// Validates and constructs a recipe line set, enforcing R3′/R4/R5/N1/N2/N3 (see the type summary).
    /// Returns the first blocking <see cref="Error"/> on failure. <paramref name="owningRecipeId"/> is the
    /// id of the recipe the lines belong to — required for the N2 self-inclusion check.
    /// </summary>
    public static Result<RecipeLineSet> Create(
        IReadOnlyList<IngredientLine> ingredients,
        IReadOnlyList<InclusionLine> inclusions,
        RecipeId owningRecipeId)
    {
        // R3′ — at least one ingredient OR inclusion
        if (ingredients.Count == 0 && inclusions.Count == 0)
            return Error.Custom("Recipes.NoIngredients",
                "A recipe must have at least one ingredient or included recipe.");

        // R4 — every ingredient ProductId non-null / non-empty
        foreach (var line in ingredients)
        {
            if (line.ProductId == Guid.Empty)
                return Error.Custom("Recipes.InvalidProductId", "Each ingredient must reference a product.");
        }

        // R5 — ingredient qty/unit both-set or both-null
        foreach (var line in ingredients)
        {
            if (line.Quantity.HasValue != line.UnitId.HasValue)
                return Error.Custom("Recipes.QtyUnitMismatch",
                    "Quantity and UnitId must both be set or both be null.");
        }

        // N1 — every inclusion serving count > 0
        foreach (var inc in inclusions)
        {
            if (inc.Servings <= 0)
                return Error.Custom("Recipes.InvalidInclusionServings",
                    "An included recipe must specify a positive number of servings.");
        }

        // N2 — no self-inclusion (the degenerate cycle)
        foreach (var inc in inclusions)
        {
            if (inc.SubRecipeId == owningRecipeId)
                return Error.Custom("Recipes.SelfInclusion",
                    "A recipe cannot include itself.");
        }

        // N3 (R6 widened) — ordinals contiguous from min value across the union of BOTH line types
        var ordinals = ingredients.Select(l => l.Ordinal)
            .Concat(inclusions.Select(l => l.Ordinal))
            .OrderBy(o => o)
            .ToList();
        var minOrdinal = ordinals[0];
        for (var i = 0; i < ordinals.Count; i++)
        {
            if (ordinals[i] != minOrdinal + i)
                return Error.Custom("Recipes.NonContiguousOrdinals",
                    "Recipe line ordinals must be contiguous across ingredients and inclusions.");
        }

        return new RecipeLineSet(ingredients, inclusions);
    }
}
