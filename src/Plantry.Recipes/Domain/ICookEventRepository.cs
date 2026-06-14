namespace Plantry.Recipes.Domain;

/// <summary>
/// Persistence port for the append-only <see cref="CookEvent"/> aggregate (R8).
/// Implemented in Plantry.Recipes.Infrastructure.
/// </summary>
public interface ICookEventRepository
{
    /// <summary>Stages a new <see cref="CookEvent"/> for insertion. Call <see cref="SaveChangesAsync"/> to commit.</summary>
    Task AddAsync(CookEvent cookEvent, CancellationToken ct = default);

    /// <summary>
    /// Returns the cook history for a single recipe, ordered by <c>cooked_at desc</c> (most recent first).
    /// Feeds the future cook-history read model on the recipe detail page.
    /// </summary>
    Task<IReadOnlyList<CookEvent>> ListByRecipeAsync(RecipeId recipeId, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
