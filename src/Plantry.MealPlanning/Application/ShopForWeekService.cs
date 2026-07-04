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
///
/// <para>
/// <b>Per-slot provenance (plantry-jie7):</b> missing items are accumulated and written
/// <em>per planned_meal slot</em>, and each slot's <see cref="IMealPlanShoppingWriter.AddItemsAsync"/>
/// call stamps <c>sourceRef = meal.Id</c> (the <c>planned_meal</c> slot id — NOT the whole-plan id).
/// Shopping's per-source contribution model (plantry-9scq) keys contributions by (Source, SourceRef),
/// so the same product needed by two slots yields ONE product line carrying two contributions that
/// SUM (no line fan-out, total unchanged), and the slot ids resolve through
/// <c>IShoppingMealPlanReader.GetMealPlanSlotsAsync</c> to the "for {Day} {meal}" board labels
/// (plantry-jwyb). A product needed by multiple slots contributes in a single canonical unit — the
/// first unit seen for that product across the week — so the per-slot contributions stay mergeable.
/// </para>
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

        // Week-level canonical unit per product: the first unit seen for a product anywhere in the
        // week. Reused for every slot's contribution of that product so cross-slot contributions
        // stay in one unit and merge into a single line (recipe ingredients share default units with
        // stock, so this is normally the only unit seen).
        var unitByProduct = new Dictionary<Guid, Guid>();

        // Distinct product lines added across the whole week — the user-facing "N items" count.
        // Contributions may be many (one per slot per product) but a product is one line.
        var productLines = new HashSet<Guid>();

        foreach (var meal in plan.PlannedMeals)
        {
            // Note-meals have no dishes — skip (M13 / domain-model §3.2).
            if (meal.Note is not null || meal.PlannedDishes.Count == 0)
                continue;

            // Per-slot accumulation: productId → summed quantity for THIS slot only. The unit is the
            // week-level canonical unit (unitByProduct), recorded below as each product is first seen.
            var slotMissing = new Dictionary<Guid, decimal>();

            foreach (var dish in meal.PlannedDishes)
            {
                if (dish.RecipeId.HasValue)
                {
                    // Recipe dish: collect missing/low ingredients from Recipes' read model.
                    var ingredients = await recipeReader.GetMissingIngredientsAsync(
                        dish.RecipeId.Value, dish.Servings, ct);

                    foreach (var ing in ingredients)
                    {
                        RecordFirstUnit(ing.ProductId, ing.UnitId);
                        slotMissing[ing.ProductId] =
                            slotMissing.GetValueOrDefault(ing.ProductId) + ing.Quantity;
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
                            RecordFirstUnit(dish.ProductId.Value, unitId);
                            slotMissing[dish.ProductId.Value] =
                                slotMissing.GetValueOrDefault(dish.ProductId.Value) + needed;
                        }
                    }
                }
            }

            if (slotMissing.Count == 0)
                continue;

            var slotItems = slotMissing
                .Select(kvp => new MealPlanShoppingItem(kvp.Key, kvp.Value, unitByProduct[kvp.Key]))
                .ToList();

            // Stamp the planned_meal SLOT id (NOT plan.Id) so the shopping board resolves the
            // per-slot "for {Day} {meal}" label (plantry-jwyb) and slot contributions sum (plantry-9scq).
            await shoppingWriter.AddItemsAsync(
                slotItems, source: "meal_plan", sourceRef: meal.Id.Value, ct);

            foreach (var productId in slotMissing.Keys)
                productLines.Add(productId);
        }

        if (productLines.Count == 0)
            return new ShopForWeekResult(0);

        logger.LogInformation(
            "ShopForWeek added {ItemCount} missing product line(s) to shopping list for week {WeekStart}.",
            productLines.Count, weekStart);

        return new ShopForWeekResult(productLines.Count);

        // Records the first unit seen for a product across the week (idempotent thereafter), so every
        // slot's contribution of that product uses one canonical unit and the contributions merge.
        void RecordFirstUnit(Guid productId, Guid unitId)
        {
            if (!unitByProduct.ContainsKey(productId))
                unitByProduct[productId] = unitId;
        }
    }
}

/// <summary>Result of a ShopForWeek execution.</summary>
/// <param name="ItemsAdded">Number of distinct product lines added to the shopping list (0 = fully stocked).</param>
public sealed record ShopForWeekResult(int ItemsAdded);
