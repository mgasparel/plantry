using Microsoft.Extensions.Logging;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;

namespace Plantry.MealPlanning.Application;

/// <summary>
/// Application service for "Shop for this week" (J6, domain-model §7).
/// Across all PlannedDishes in the week:
/// - Recipe dishes → their Missing/Low ingredients at planned servings (via IRecipeReadModel).
/// - Product dishes → the product itself if short on stock (via IMealPlanStockReader).
/// Note-meals are skipped (no dishes by construction).
/// Quantities are summed per product (excluding untracked staples) before calling
/// IMealPlanShoppingWriter.AddItemsAsync with source="meal_plan" (DM-18, P2-4 seam reused).
/// </summary>
public sealed class ShopForWeekService(
    IMealPlanRepository mealPlanRepo,
    IRecipeReadModel recipeReader,
    IMealPlanStockReader stockReader,
    IMealPlanShoppingWriter shoppingWriter,
    ILogger<ShopForWeekService> logger)
{
    /// <summary>
    /// Collects all missing items for the week and adds them to the shopping list.
    /// Returns the number of distinct product lines added (may be 0 when everything is in stock).
    /// </summary>
    public async Task<ShopForWeekResult> ExecuteAsync(
        HouseholdId householdId,
        DateOnly weekStart,
        CancellationToken ct = default)
    {
        var plan = await mealPlanRepo.FindByWeekAsync(householdId, weekStart, ct);
        if (plan is null || plan.PlannedMeals.Count == 0)
            return new ShopForWeekResult(0);

        // Accumulate required quantities per productId.
        // key = productId; value = (totalQuantity, unitId)
        // When a product appears via both recipe-ingredient and product-dish paths the unit must
        // match — we take the first unit seen (recipe ingredients share default units with stock).
        var missing = new Dictionary<Guid, (decimal Qty, Guid UnitId)>();

        foreach (var meal in plan.PlannedMeals)
        {
            // Note-meals have no dishes — skip (M13 / domain-model §3.2).
            if (meal.Note is not null || meal.PlannedDishes.Count == 0)
                continue;

            foreach (var dish in meal.PlannedDishes)
            {
                if (dish.RecipeId.HasValue)
                {
                    // Recipe dish: collect missing/low ingredients from Recipes' read model.
                    var ingredients = await recipeReader.GetMissingIngredientsAsync(
                        dish.RecipeId.Value, dish.Servings, ct);

                    foreach (var ing in ingredients)
                    {
                        if (missing.TryGetValue(ing.ProductId, out var existing))
                            missing[ing.ProductId] = (existing.Qty + ing.Quantity, existing.UnitId);
                        else
                            missing[ing.ProductId] = (ing.Quantity, ing.UnitId);
                    }
                }
                else if (dish.ProductId.HasValue)
                {
                    // Product dish: add the product itself if short on stock.
                    // Servings for a product dish = quantity in the product's default unit (domain-model §3.3).
                    var stock = await stockReader.FindStockAsync(dish.ProductId.Value, ct);

                    // Null stock = never stocked = definitely missing.
                    var available = stock?.AvailableQuantity ?? 0m;
                    if (available < dish.Servings)
                    {
                        var needed = dish.Servings - available;
                        // Unit: use the stock's default unit when available (ensures consistent units);
                        // fall back to a zeroed-out unit ref — caller should always have a stock record
                        // for a tracked product dish, but be defensive.
                        var unitId = stock?.DefaultUnitId ?? Guid.Empty;

                        if (unitId != Guid.Empty)
                        {
                            if (missing.TryGetValue(dish.ProductId.Value, out var existing))
                                missing[dish.ProductId.Value] = (existing.Qty + needed, existing.UnitId);
                            else
                                missing[dish.ProductId.Value] = (needed, unitId);
                        }
                    }
                }
            }
        }

        if (missing.Count == 0)
            return new ShopForWeekResult(0);

        var items = missing
            .Select(kvp => new MealPlanShoppingItem(kvp.Key, kvp.Value.Qty, kvp.Value.UnitId))
            .ToList();

        await shoppingWriter.AddItemsAsync(items, source: "meal_plan", sourceRef: plan.Id.Value, ct);

        logger.LogInformation(
            "ShopForWeek added {ItemCount} missing product line(s) to shopping list for week {WeekStart}.",
            items.Count, weekStart);

        return new ShopForWeekResult(items.Count);
    }
}

/// <summary>Result of a ShopForWeek execution.</summary>
/// <param name="ItemsAdded">Number of distinct product lines added to the shopping list (0 = fully stocked).</param>
public sealed record ShopForWeekResult(int ItemsAdded);
