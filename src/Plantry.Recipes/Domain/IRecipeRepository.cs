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

    /// <summary>
    /// Lists all non-archived household recipes for the Browse page (J1/J2, recipes.md resolved call 6).
    /// Loads ingredients and tag memberships but NOT the recipe_photo (resolved call 3 — photo is
    /// loaded lazily via a separate image endpoint when gallery view requests the thumbnail).
    /// Ordered by name for a stable default query; final sort is applied in the application layer.
    /// </summary>
    Task<IReadOnlyList<Recipe>> ListForBrowseAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns true if the household has at least one non-archived recipe — used for the
    /// Today-page cold-start check to avoid materializing the full browse list.
    /// </summary>
    Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default);
}
