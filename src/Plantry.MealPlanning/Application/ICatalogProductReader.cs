namespace Plantry.MealPlanning.Application;

/// <summary>
/// Anti-corruption read port onto the Catalog context for the MealPlanning context.
/// Checks whether a product referenced as a dish actually exists in this household's catalog.
/// Implemented in Plantry.Web over CatalogDbContext.
/// Note: a same-named interface exists in Plantry.Recipes.Application — this is the MealPlanning copy,
/// intentionally separate to avoid introducing a cross-context dependency.
/// </summary>
public interface IMealPlanCatalogProductReader
{
    /// <summary>Returns true when the product exists in this household's catalog (is not archived).</summary>
    Task<bool> ExistsAsync(Guid productId, CancellationToken ct = default);

    /// <summary>
    /// Name search for the product search in the meal editor.
    /// Returns up to <paramref name="maxResults"/> active catalog products whose name contains the query.
    /// </summary>
    Task<IReadOnlyList<MealPlanProductReadModel>> SearchAsync(string nameQuery, int maxResults = 20, CancellationToken ct = default);

    /// <summary>
    /// Resolves product names by ID in a single round-trip.
    /// Ids absent from the catalog are simply omitted from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(IReadOnlyList<Guid> productIds, CancellationToken ct = default);
}

/// <summary>Display facts for a catalog product in the meal editor.</summary>
public sealed record MealPlanProductReadModel(Guid ProductId, string Name);
