using Plantry.Recipes.Domain;

namespace Plantry.Recipes.Application;

/// <summary>
/// Computes the per-ingredient shortfall for a recipe at a given serving count.
/// Shared by both the J5 (AddMissingToShoppingList) and J6 (ShopForWeek via GetMissingIngredientsAsync)
/// seams so they cannot drift.
///
/// <para>For each tracked ingredient that is <see cref="IngredientStatus.Missing"/> or
/// <see cref="IngredientStatus.Low"/>, emits a <see cref="IngredientShortfall"/> with:</para>
/// <list type="bullet">
///   <item>shortfall = max(0, scaledRequired − available) — what the household still needs to buy.</item>
///   <item>For Missing lines available is 0, so shortfall equals the full scaled required quantity.</item>
///   <item>InStock and Untracked lines are never emitted (C12 / recipes-journeys.md J5).</item>
///   <item>Lines with null Quantity or null UnitId are skipped (untracked staple or malformed, R5/C12).</item>
///   <item>Lines with a computed shortfall of zero are skipped (would add nothing to the list).</item>
/// </list>
/// </summary>
public static class RecipeShortfallCalculator
{
    /// <summary>
    /// Computes the shortfall lines for a recipe at the desired serving count.
    /// </summary>
    /// <param name="recipe">The loaded recipe aggregate (ingredients must be populated).</param>
    /// <param name="fulfillment">A fresh <see cref="FulfillmentResult"/> at <paramref name="desiredServings"/>.</param>
    /// <param name="desiredServings">The serving count the fulfillment was computed at.</param>
    /// <returns>One shortfall line per ingredient that is Missing or Low with a positive deficit.</returns>
    public static IReadOnlyList<IngredientShortfall> Compute(
        Recipe recipe,
        FulfillmentResult fulfillment,
        int desiredServings)
    {
        var scale = (decimal)desiredServings / recipe.DefaultServings;

        // Index ingredient lines by IngredientId for O(1) lookup.
        var ingredientIndex = recipe.Ingredients.ToDictionary(i => i.Id);

        var results = new List<IngredientShortfall>();

        foreach (var line in fulfillment.Lines)
        {
            // Only Missing and Low lines — InStock and Untracked are satisfied (C12 / J5).
            if (line.Status is not (IngredientStatus.Missing or IngredientStatus.Low))
                continue;

            if (!ingredientIndex.TryGetValue(line.IngredientId, out var ingredient))
                continue; // defensive: should not happen if FulfillmentService and Recipe are consistent

            // Untracked staples have null Quantity/UnitId (R5) — skip (C12).
            // FulfillmentService classifies them as Untracked, but guard here for safety.
            if (ingredient.Quantity is null || ingredient.UnitId is null)
                continue;

            var required = ingredient.Quantity.Value * scale;

            // AvailableQuantity is already in the ingredient's unit (FulfillmentService.cs).
            // For Missing lines it is null, which we treat as 0.
            var available = line.AvailableQuantity ?? 0m;

            var shortfall = Math.Max(0m, required - available);
            if (shortfall <= 0m)
                continue; // nothing to buy for this line

            results.Add(new IngredientShortfall(ingredient.ProductId, shortfall, ingredient.UnitId.Value));
        }

        return results;
    }
}

/// <summary>
/// A single ingredient shortfall: the quantity of a product the household still needs to buy
/// to cook a recipe at the desired serving count.
/// </summary>
/// <param name="ProductId">Soft ref → catalog.product (DM-3).</param>
/// <param name="ShortfallQuantity">
/// max(0, scaledRequired − available) — what still needs to be purchased.
/// In the ingredient's declared unit.
/// </param>
/// <param name="UnitId">Soft ref → catalog.unit (DM-3).</param>
public sealed record IngredientShortfall(Guid ProductId, decimal ShortfallQuantity, Guid UnitId);
