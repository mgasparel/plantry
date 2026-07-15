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

    /// <summary>
    /// Resolves which of the given recipe ids exist in the household — the sub-recipe existence check
    /// <c>AuthorRecipe</c> runs for each inclusion (recipe-composition.md N4), in a single round-trip
    /// instead of an await-per-id <see cref="GetByIdAsync"/> loop (plantry-xgmb). Existence semantics
    /// match <see cref="GetByIdAsync"/>: RLS/query-filter scoped, archived recipes included; no
    /// navigation properties are loaded (PK projection only).
    ///
    /// <para>The default implementation falls back to a per-id <see cref="GetByIdAsync"/> loop so test
    /// doubles need not reimplement it; the production repository overrides it with one batched query.</para>
    /// </summary>
    async Task<IReadOnlySet<RecipeId>> ResolveExistingIdsAsync(IReadOnlyList<RecipeId> ids, CancellationToken ct = default)
    {
        var found = new HashSet<RecipeId>();
        foreach (var id in ids)
            if (await GetByIdAsync(id, ct) is not null)
                found.Add(id);
        return found;
    }

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

    /// <summary>
    /// Returns every inclusion edge (parent → sub) in the current household (recipe-composition.md N4/D5).
    /// The application layer walks this graph at save to reject cycles (N4); household recipe counts make
    /// the full-edge load trivially cheap. Household-scoped by the RLS query filter (ADR-008).
    /// </summary>
    Task<IReadOnlyList<RecipeInclusionEdge>> ListInclusionEdgesAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the ids of recipes that include <paramref name="subRecipeId"/> via an inclusion line
    /// (recipe-composition.md N5/D12). When <paramref name="transitive"/> is false (the default) only
    /// direct includers are returned — the set the N5 archival guard counts ("used by N recipes"); when
    /// true the reverse graph is walked to include indirect includers. Household-scoped by the RLS query
    /// filter (ADR-008).
    /// </summary>
    Task<IReadOnlySet<RecipeId>> GetIncluderIdsAsync(
        RecipeId subRecipeId, bool transitive = false, CancellationToken ct = default);
}

/// <summary>One directed inclusion edge: <see cref="ParentId"/> includes <see cref="SubId"/> (N4 graph).</summary>
public readonly record struct RecipeInclusionEdge(RecipeId ParentId, RecipeId SubId);
