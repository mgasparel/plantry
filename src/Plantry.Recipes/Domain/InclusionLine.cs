namespace Plantry.Recipes.Domain;

/// <summary>
/// Input DTO for <see cref="Recipe.ReplaceLines"/> — one ordered inclusion line from the author
/// (recipe-composition.md §3). <see cref="Ordinal"/> is in the SHARED ordinal space with
/// <see cref="IngredientLine"/> (N3 requires the union of both line types to be contiguous).
/// N1: <see cref="Servings"/> must be &gt; 0. N2: <see cref="SubRecipeId"/> must not equal the owning
/// recipe. Same-household reference, sub-existence, and the DAG check (N4) are enforced by the
/// application layer before this DTO is assembled.
/// </summary>
public sealed record InclusionLine(
    RecipeId SubRecipeId,
    decimal Servings,
    string? GroupHeading,
    int Ordinal);
