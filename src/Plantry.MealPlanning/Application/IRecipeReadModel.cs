namespace Plantry.MealPlanning.Application;

/// <summary>
/// Anti-corruption read port onto the Recipes context for the MealPlanning context.
/// Supplies the minimal facts needed to display and validate a recipe dish.
/// Implemented in Plantry.Web over RecipesDbContext.
/// </summary>
public interface IRecipeReadModel
{
    /// <summary>
    /// Returns a recipe summary for display in the meal editor.
    /// Returns null when the recipe does not exist in this household.
    /// </summary>
    Task<RecipeReadModel?> GetByIdAsync(Guid recipeId, CancellationToken ct = default);

    /// <summary>
    /// Name search for the recipe search in the meal editor.
    /// Returns up to <paramref name="maxResults"/> recipes whose name contains the query.
    /// Empty/whitespace query returns a short list of all recipes.
    /// </summary>
    Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string nameQuery, int maxResults = 20, CancellationToken ct = default);

    /// <summary>
    /// Returns live fulfillment and cost facts for a recipe at the given serving count.
    /// Computed fresh by Recipes' domain services (FulfillmentService / CostingService) via the
    /// Inventory and Pricing ports — MealPlanning borrows these, never recomputes them (domain-model §1).
    /// Returns null when the recipe does not exist in this household.
    /// </summary>
    Task<RecipeDishEnrichment?> GetEnrichmentAsync(
        Guid recipeId,
        int servings,
        DateOnly today,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the ingredients that are Missing or Low at <paramref name="servings"/> for a recipe.
    /// Used by ShopForWeek to aggregate the shopping list (J6).
    /// Returns an empty list when the recipe does not exist, has no ingredients, or everything is in stock.
    /// </summary>
    Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(
        Guid recipeId,
        int servings,
        CancellationToken ct = default);

    /// <summary>
    /// Returns <see langword="true"/> when ANY recipe in the household's full recipe corpus carries
    /// <paramref name="tagId"/> — regardless of the 50-cap candidate list from <see cref="SearchAsync"/>.
    /// Used by <see cref="Plantry.MealPlanning.Domain.UnfulfillabilityDetector"/> for feasibility
    /// pre-checks: a confident "you have no vegetarian recipes" would be wrong if recipes outside
    /// the top-50 carry the tag. This is a targeted, cheap corpus query.
    /// Returns <see langword="false"/> when no non-archived recipe carries the tag.
    /// </summary>
    Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default);
}

/// <summary>Display facts for a recipe in the meal editor.</summary>
/// <param name="HasPhoto">True when the recipe has a stored photo (served at
/// <c>/Recipes/Details?id={RecipeId}&amp;handler=Photo</c>) — lets the dish picker show a thumbnail
/// and fall back to an initial chip when absent.</param>
public sealed record RecipeReadModel(
    Guid RecipeId,
    string Name,
    IReadOnlyList<Guid> TagIds,
    int DefaultServings,
    bool HasPhoto = false);

/// <summary>
/// Live fulfillment and cost enrichment for a recipe dish at a given serving count.
/// Borrowed from Recipes' read models — MealPlanning rolls these up, never recomputes (domain-model §1).
/// </summary>
/// <param name="FulfillmentPercent">
/// 0–100 percentage of tracked ingredients that are fully In Stock at the requested servings.
/// 100 = fully cookable; 0 = nothing in stock. Untracked staples are excluded (C12).
/// </param>
/// <param name="TotalCost">
/// Estimated total cost for all servings; null when no ingredients have pricing data.
/// </param>
/// <param name="CostIsPartial">
/// True when the cost estimate covers only some ingredients (partial pricing data).
/// </param>
/// <param name="HasExpiringIngredients">
/// True when any tracked ingredient has stock expiring within 4 days ("Use soon" flag, J1 step 4).
/// </param>
public sealed record RecipeDishEnrichment(
    int FulfillmentPercent,
    decimal? TotalCost,
    bool CostIsPartial,
    bool HasExpiringIngredients);

/// <summary>
/// One missing (or low-stock) ingredient for a recipe at a given serving count, for the ShopForWeek flow.
/// Untracked staples are never included (C12 — always satisfied).
/// </summary>
/// <param name="ProductId">Soft ref → catalog.product (DM-3).</param>
/// <param name="Quantity">
/// Shortfall quantity — max(0, scaledRequired − available) — what the household still needs to buy.
/// For Missing lines (zero available) this equals the full scaled required quantity.
/// </param>
/// <param name="UnitId">Soft ref → catalog.unit (DM-3).</param>
public sealed record RecipeMissingIngredient(Guid ProductId, decimal Quantity, Guid UnitId);
