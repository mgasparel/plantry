namespace Plantry.Shopping.Application;

/// <summary>
/// Anti-corruption read port: Shopping's read model needs recipe names from the Recipes context
/// to resolve Recipe-source contribution labels for the attribution sub-line on the shopping board.
/// This interface is defined in Shopping.Application and implemented in the Web layer (an adapter
/// over <c>RecipesDbContext</c>), following the same ACL pattern as <see cref="IShoppingCatalogReader"/>
/// and <see cref="IShoppingPantryReader"/> (ADR-002 — Shopping must NOT read Recipes' EF context directly).
///
/// <para>
/// Resolution is only needed for <c>ItemSource.Recipe</c> contributions where
/// <c>SourceRef</c> is the recipe id. Manual contributions resolve to a fixed label; MealPlan and Deal
/// contributions resolve via their own sibling ports (<see cref="IShoppingMealPlanReader"/> and
/// <see cref="IShoppingDealAttributionReader"/>, plantry-jwyb), not this one.
/// </para>
/// </summary>
public interface IShoppingRecipeReader
{
    /// <summary>
    /// Resolves recipe names for a set of recipe ids in one batch call.
    /// Recipe ids not found in the household (deleted or belonging to another household) are
    /// silently omitted from the result dictionary.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetRecipeNamesAsync(
        IReadOnlyList<Guid> recipeIds,
        CancellationToken ct = default);
}
