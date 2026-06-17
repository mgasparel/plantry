namespace Plantry.MealPlanning.Application;

/// <summary>
/// Anti-corruption write port onto the Shopping context for the MealPlanning context (DM-18, P2-4 seam).
/// Reuses the same contract as <c>Plantry.Recipes.Application.IShoppingListWriter</c> but is owned by
/// MealPlanning.Application to keep MealPlanning free of Recipes dependencies (DM-3).
/// Implemented in Plantry.Web over the same <c>ShoppingListWriterAdapter</c>.
/// </summary>
public interface IMealPlanShoppingWriter
{
    /// <summary>
    /// Adds a batch of items to the household's shopping list with provenance
    /// <c>source="meal_plan"</c> and <c>sourceRef=mealPlanId</c>.
    /// Shopping applies its merge rule (DM-18) — existing unchecked items for the same product
    /// have their quantity incremented rather than duplicated.
    /// </summary>
    Task AddItemsAsync(
        IEnumerable<MealPlanShoppingItem> items,
        string source,
        Guid sourceRef,
        CancellationToken ct = default);
}

/// <summary>
/// One product-backed item in a bulk add-to-shopping-list call (DM-18).
/// Mirrors <c>Plantry.Recipes.Application.ShoppingItem</c> — a separate copy per context (DM-3).
/// </summary>
/// <param name="ProductId">Soft ref → catalog.product (DM-3).</param>
/// <param name="Quantity">Required quantity, scaled to the desired serving count.</param>
/// <param name="UnitId">Soft ref → catalog.unit (DM-3).</param>
public sealed record MealPlanShoppingItem(Guid ProductId, decimal Quantity, Guid UnitId);
