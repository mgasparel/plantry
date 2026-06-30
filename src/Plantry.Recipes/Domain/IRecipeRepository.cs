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
    /// Returns the set of recipe ids that have a stored photo, scoped to the current household by
    /// the RLS query filter. Selects only the PK column — no photo bytes are loaded.
    /// Used by <see cref="BrowseRecipesQuery"/> to populate <c>HasPhoto</c> without an eager Include
    /// that would drag the <c>bytea</c> column into the browse query (resolved call 3).
    /// </summary>
    Task<IReadOnlySet<RecipeId>> ListRecipeIdsWithPhotoAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns true if the household has at least one non-archived recipe — used for the
    /// Today-page cold-start check to avoid materializing the full browse list.
    /// </summary>
    Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default);

    /// <summary>
    /// Resolves recipe names for the given set of recipe ids (plantry-26g, Shopping→Recipes ACL).
    /// Returns a dictionary of id → name for non-archived recipes whose id is in <paramref name="ids"/>.
    /// Ids not found in the household (deleted or not accessible via RLS) are silently omitted.
    /// No navigation properties are loaded — this is a lightweight name-projection query only.
    /// </summary>
    Task<IReadOnlyDictionary<RecipeId, string>> GetRecipeNamesByIdAsync(IReadOnlyList<RecipeId> ids, CancellationToken ct = default);
}
