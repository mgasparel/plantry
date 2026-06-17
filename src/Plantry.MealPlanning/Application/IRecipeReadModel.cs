namespace Plantry.MealPlanning.Application;

/// <summary>
/// Anti-corruption read port onto the Recipes context for the MealPlanning context.
/// Supplies the minimal facts needed to display and validate a recipe dish.
/// Implemented in Plantry.Web over RecipesDbContext.
/// </summary>
public interface IRecipeReadModel
{
    /// <summary>
    /// Returns a recipe summary for display in the meal editor.
    /// Returns null when the recipe does not exist in this household.
    /// </summary>
    Task<RecipeReadModel?> GetByIdAsync(Guid recipeId, CancellationToken ct = default);

    /// <summary>
    /// Name search for the recipe search in the meal editor.
    /// Returns up to <paramref name="maxResults"/> recipes whose name contains the query.
    /// Empty/whitespace query returns a short list of all recipes.
    /// </summary>
    Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string nameQuery, int maxResults = 20, CancellationToken ct = default);
}

/// <summary>Display facts for a recipe in the meal editor.</summary>
public sealed record RecipeReadModel(
    Guid RecipeId,
    string Name,
    IReadOnlyList<Guid> TagIds,
    int DefaultServings);
