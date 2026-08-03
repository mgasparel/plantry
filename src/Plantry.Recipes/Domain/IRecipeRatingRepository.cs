namespace Plantry.Recipes.Domain;

public interface IRecipeRatingRepository
{
    Task AddAsync(RecipeRating rating, CancellationToken ct = default);

    /// <summary>Removes an existing rating row (the "clear" / absence-of-a-row path).</summary>
    void Remove(RecipeRating rating);

    /// <summary>
    /// Finds the single rating row for (recipe, user) in the current household — the UNIQUE
    /// (household_id, recipe_id, user_id) lookup the upsert/clear commands key on.
    /// </summary>
    Task<RecipeRating?> FindAsync(RecipeId recipeId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns every rating for a single recipe, household-scoped by the RLS query filter — the
    /// per-member breakdown source (popover/Details, plantry-zlwp.1) and the flat MyStars/HouseholdAvg
    /// projection for a single-recipe view.
    /// </summary>
    Task<IReadOnlyList<RecipeRating>> ListByRecipeAsync(RecipeId recipeId, CancellationToken ct = default);

    /// <summary>
    /// Returns every rating across the given recipes in ONE round-trip — the batched lookup
    /// <see cref="Plantry.Recipes.Application.BrowseRecipesQuery"/> uses to compute MyStars/HouseholdAvg/
    /// RatedCount per row without a per-recipe query in its build loop (mirrors
    /// <see cref="IRecipeRepository.ListRecipeIdsWithPhotoAsync"/>'s batch-then-thread-through shape).
    /// </summary>
    Task<IReadOnlyList<RecipeRating>> ListByRecipeIdsAsync(
        IReadOnlyList<RecipeId> recipeIds, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
