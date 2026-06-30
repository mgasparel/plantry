using Plantry.Recipes.Domain;
using Plantry.Shopping.Application;

namespace Plantry.Web.Shopping;

/// <summary>
/// Web-layer adapter implementing <see cref="IShoppingRecipeReader"/> over the Recipes bounded
/// context's <see cref="IRecipeRepository"/>. This is the anti-corruption layer seam between
/// Shopping and Recipes — Shopping never takes a direct dependency on the Recipes EF context or
/// repositories (ADR-002). Follows the same adapter pattern as <c>ShoppingCatalogReaderAdapter</c>
/// (Shopping → Catalog ACL) and <c>ShoppingPantryReaderAdapter</c> (Shopping → Inventory ACL).
///
/// <para>
/// The adapter calls <see cref="IRecipeRepository.GetRecipeNamesByIdAsync"/> — a lightweight
/// name-projection query with no navigation includes (no Ingredients/Tags/Photo). The RLS
/// interceptor on the underlying EF context scopes the query to the current household automatically
/// (ADR-008 defense-in-depth).
/// </para>
/// </summary>
public sealed class ShoppingRecipeReaderAdapter(IRecipeRepository recipes) : IShoppingRecipeReader
{
    public async Task<IReadOnlyDictionary<Guid, string>> GetRecipeNamesAsync(
        IReadOnlyList<Guid> recipeIds,
        CancellationToken ct = default)
    {
        if (recipeIds.Count == 0)
            return new Dictionary<Guid, string>();

        // Convert raw Guid SourceRef values to typed RecipeId for the domain repository call.
        var typedIds = recipeIds.Select(RecipeId.From).ToList();

        // Lightweight name-only projection — no navigation includes (no Ingredients/Tags/Photo).
        var namesByRecipeId = await recipes.GetRecipeNamesByIdAsync(typedIds, ct);

        // Return the result keyed by the raw Guid (as used in ShoppingListItemContribution.SourceRef).
        return namesByRecipeId.ToDictionary(kvp => kvp.Key.Value, kvp => kvp.Value);
    }
}
