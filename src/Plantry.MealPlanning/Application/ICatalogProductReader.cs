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
    /// Returns true when the product exists (is not archived) AND is a concrete, non-parent product.
    /// A parent (grouping) product has no resolution point for "which variant was consumed" — no
    /// price observation, no stock record, no fulfillment target — so it cannot be planned as a
    /// direct product dish. Used to gate new dish creation; existing planned dishes that already
    /// reference a parent are grandfathered and unaffected by this check.
    /// </summary>
    Task<bool> IsPlannableAsync(Guid productId, CancellationToken ct = default);

    /// <summary>
    /// Name search for the product search in the meal editor.
    /// Returns up to <paramref name="maxResults"/> active catalog products whose name contains the
    /// query. Parent (grouping) products are excluded — only their concrete variants are plannable
    /// as direct product dishes; variants and unrelated leaf products are returned as usual.
    /// </summary>
    Task<IReadOnlyList<MealPlanProductReadModel>> SearchAsync(string nameQuery, int maxResults = 20, CancellationToken ct = default);

    /// <summary>
    /// Resolves product names by ID in a single round-trip.
    /// Ids absent from the catalog are simply omitted from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(IReadOnlyList<Guid> productIds, CancellationToken ct = default);

    /// <summary>
    /// Returns the product's default unit id, or null when the product is unresolvable (plantry-9n7l).
    /// Used by <see cref="Plantry.MealPlanning.Domain.PlanCostingService"/> to convert a price
    /// observation's unit onto the product's default unit before costing a product-dish — a price
    /// observation can be recorded in a unit other than the product's default (e.g. a weight-priced
    /// Intake line records the receipt's resolved weight unit independent of the product's own default,
    /// <c>CommitSessionCommand.cs</c>), so the two must not be assumed equal.
    /// Default implementation returns null (unresolvable) so existing implementers/test doubles compile
    /// unchanged; the production adapter overrides it.
    /// </summary>
    Task<Guid?> FindDefaultUnitIdAsync(Guid productId, CancellationToken ct = default)
        => Task.FromResult<Guid?>(null);
}

/// <summary>Display facts for a catalog product in the meal editor.</summary>
public sealed record MealPlanProductReadModel(Guid ProductId, string Name);
