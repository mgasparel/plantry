using Plantry.SharedKernel;

namespace Plantry.Recipes.Domain;

public interface IRecipeRepository
{
    Task AddAsync(Recipe recipe, CancellationToken ct = default);

    /// <summary>
    /// Loads the recipe with its ingredients, tag memberships, and photo.
    /// Returns null if not found (or filtered by household RLS/query-filter).
    /// </summary>
    Task<Recipe?> GetByIdAsync(RecipeId id, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns true if a recipe with the given name already exists for this household.
    /// Used by the application layer to enforce R1 (name uniqueness).
    /// </summary>
    Task<bool> NameExistsAsync(HouseholdId householdId, string name, CancellationToken ct = default);
}
