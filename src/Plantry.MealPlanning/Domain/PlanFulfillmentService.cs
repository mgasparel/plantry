using Plantry.MealPlanning.Application;

namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Domain service that rolls up fulfillment for a planned meal or a full week (domain-model §7).
/// Stateless. Borrows Recipes' FulfillmentResult via <see cref="IRecipeReadModel.GetEnrichmentAsync"/>
/// for recipe dishes; resolves product-dish fulfillment from <see cref="IMealPlanStockReader"/>
/// directly. Note-meals contribute nothing.
///
/// MealPlanning owns no fulfillment engine — it rolls up what Recipes already computes (domain-model §1).
/// The 4-day "Use soon" threshold mirrors <c>FulfillmentService.ExpiringSoonDays</c> in Recipes.
/// </summary>
public sealed class PlanFulfillmentService(
    IRecipeReadModel recipeReader,
    IMealPlanStockReader stockReader)
{
    /// <summary>Days before expiry to flag as "Use soon" (mirrors Recipes FulfillmentService).</summary>
    public const int ExpiringSoonDays = 4;

    /// <summary>
    /// Computes the rolled-up fulfillment for a single <see cref="PlannedMeal"/>.
    /// Note-meals return <see cref="MealFulfillment.None"/>.
    /// </summary>
    public async Task<MealFulfillment> RollUpMealAsync(
        PlannedMeal meal,
        DateOnly today,
        CancellationToken ct = default)
    {
        // Note-meals have no dishes — contribute nothing (domain-model §3.2).
        if (meal.Note is not null || meal.PlannedDishes.Count == 0)
            return MealFulfillment.None;

        var parts = new List<DishFulfillment>(meal.PlannedDishes.Count);
        foreach (var dish in meal.PlannedDishes)
        {
            var df = await ComputeDishFulfillmentAsync(dish, today, ct);
            parts.Add(df);
        }

        return Aggregate(parts);
    }

    /// <summary>
    /// Rolls up fulfillment across all meals in a <see cref="MealPlan"/> for the week.
    /// </summary>
    public async Task<MealFulfillment> RollUpWeekAsync(
        MealPlan plan,
        DateOnly today,
        CancellationToken ct = default)
    {
        var parts = new List<DishFulfillment>();
        foreach (var meal in plan.PlannedMeals)
        {
            if (meal.Note is not null || meal.PlannedDishes.Count == 0)
                continue;

            foreach (var dish in meal.PlannedDishes)
            {
                var df = await ComputeDishFulfillmentAsync(dish, today, ct);
                parts.Add(df);
            }
        }

        if (parts.Count == 0)
            return MealFulfillment.None;

        return Aggregate(parts);
    }

    // ── private helpers ─────────────────────────────────────────────────────────

    private async Task<DishFulfillment> ComputeDishFulfillmentAsync(
        PlannedDish dish,
        DateOnly today,
        CancellationToken ct)
    {
        if (dish.RecipeId.HasValue)
        {
            var enrichment = await recipeReader.GetEnrichmentAsync(
                dish.RecipeId.Value, dish.Servings, today, ct);

            if (enrichment is null)
                return new DishFulfillment(0, false); // recipe not found → treat as 0%

            return new DishFulfillment(enrichment.FulfillmentPercent, enrichment.HasExpiringIngredients);
        }

        if (dish.ProductId.HasValue)
        {
            var stock = await stockReader.FindStockAsync(dish.ProductId.Value, ct);
            if (stock is null)
                return new DishFulfillment(0, false); // not tracked → 0% (missing)

            bool inStock = stock.AvailableQuantity >= dish.Servings;
            bool useSoon = stock.SoonestExpiry.HasValue &&
                           (stock.SoonestExpiry.Value.DayNumber - today.DayNumber) <= ExpiringSoonDays;

            return new DishFulfillment(inStock ? 100 : 0, useSoon);
        }

        // Should never reach here (M12 guarantees exactly one of recipe/product).
        return new DishFulfillment(0, false);
    }

    private static MealFulfillment Aggregate(IReadOnlyList<DishFulfillment> dishes)
    {
        if (dishes.Count == 0)
            return MealFulfillment.None;

        var avgPct = (int)Math.Round(dishes.Average(d => d.FulfillmentPercent));
        var hasSoon = dishes.Any(d => d.HasExpiringIngredients);

        return new MealFulfillment(avgPct, hasSoon);
    }

    private sealed record DishFulfillment(int FulfillmentPercent, bool HasExpiringIngredients);
}

// ── Value objects ────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Rolled-up fulfillment for a planned meal or a full week (computed read model — never persisted).
/// </summary>
/// <param name="FulfillmentPercent">
/// 0–100 average across all tracked dishes in the scope.
/// 100 = every dish's ingredients are fully in stock at the planned servings.
/// </param>
/// <param name="HasExpiringIngredients">
/// True when any recipe ingredient or product has stock expiring within
/// <see cref="PlanFulfillmentService.ExpiringSoonDays"/> days.
/// </param>
public sealed record MealFulfillment(int FulfillmentPercent, bool HasExpiringIngredients)
{
    /// <summary>Sentinel returned for note-meals and meals with no dishes.</summary>
    public static readonly MealFulfillment None = new(0, false);
}
