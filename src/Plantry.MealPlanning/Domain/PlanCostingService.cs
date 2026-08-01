using Plantry.MealPlanning.Application;
using Plantry.SharedKernel.Domain;

namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Domain service that rolls up estimated cost for a planned meal or a full week (domain-model §7).
/// Stateless. Deal-aware (P5-9b, DJ6): the injected <see cref="IMealPlanPriceReader"/> now returns Pricing's
/// effective (deal-aware) price — cheapest active in-window deal else latest purchase — so roll-ups (and the
/// cost-driven planning weight) reflect live sales for free, with no change here and no Deals dependency.
///
/// Recipe dishes: borrows <c>CostPerServing × servings</c> from Recipes' read models via
/// <see cref="IRecipeReadModel.GetEnrichmentAsync"/>. Product dishes: price × quantity via
/// <see cref="IMealPlanPriceReader"/>, converting the observation's unit onto the product dish's
/// saved planned unit via <see cref="IMealPlanUnitConverter"/> before multiplying by the saved quantity
/// (plantry-9n7l). Note-meals
/// contribute nothing.
///
/// MealPlanning owns no costing engine — it rolls up what Recipes already computes (domain-model §1).
/// </summary>
public sealed class PlanCostingService(
    IRecipeReadModel recipeReader,
    IMealPlanPriceReader priceReader,
    IMealPlanUnitConverter unitConverter,
    IClock clock)
{
    /// <summary>
    /// Computes the rolled-up cost for a single <see cref="PlannedMeal"/>.
    /// Note-meals return <see cref="MealCost.None"/>.
    /// </summary>
    public async Task<MealCost> RollUpMealAsync(PlannedMeal meal, CancellationToken ct = default)
    {
        if (meal.Note is not null || meal.PlannedDishes.Count == 0)
            return MealCost.None;

        var parts = new List<DishCost>(meal.PlannedDishes.Count);
        foreach (var dish in meal.PlannedDishes)
        {
            var dc = await ComputeDishCostAsync(dish, ct);
            parts.Add(dc);
        }

        return Aggregate(parts);
    }

    /// <summary>
    /// Rolls up estimated cost across all meals in a <see cref="MealPlan"/> for the week.
    /// </summary>
    public async Task<MealCost> RollUpWeekAsync(MealPlan plan, CancellationToken ct = default)
    {
        var parts = new List<DishCost>();
        foreach (var meal in plan.PlannedMeals)
        {
            if (meal.Note is not null || meal.PlannedDishes.Count == 0)
                continue;

            foreach (var dish in meal.PlannedDishes)
            {
                var dc = await ComputeDishCostAsync(dish, ct);
                parts.Add(dc);
            }
        }

        if (parts.Count == 0)
            return MealCost.None;

        return Aggregate(parts);
    }

    // ── private helpers ─────────────────────────────────────────────────────────

    private async Task<DishCost> ComputeDishCostAsync(PlannedDish dish, CancellationToken ct)
    {
        if (dish.RecipeId.HasValue)
        {
            // Borrow cost from Recipes read model — cost is already scaled to the requested servings.
            var enrichment = await recipeReader.GetEnrichmentAsync(
                dish.RecipeId.Value, dish.Servings ?? 0, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), ct);

            if (enrichment?.TotalCost is null)
                return new DishCost(null, false);

            return new DishCost(enrichment.TotalCost.Value, enrichment.CostIsPartial);
        }

        if (dish.ProductId.HasValue)
        {
            var price = await priceReader.FindLatestAsync(dish.ProductId.Value, ct);
            if (price is null)
                return new DishCost(null, false);

            // Always derive from Price / Quantity — price per ONE price.UnitId. Deliberately NOT
            // price.UnitPrice: that field is Pricing's normalized price per BASE unit of the
            // dimension (per gram, per ml), a different basis than price.UnitId whenever that unit's
            // FactorToBase != 1 (kg, lb, L, ...). Using it directly understated kg/lb-priced product
            // dishes by exactly that factor (~1000x for kg) — plantry-9n7l, the same unit-basis
            // mismatch CostingService.ComputeLineCore had, fixed in plantry-1oca. See
            // MealPlanPricePoint.UnitPrice's doc for the full explanation.
            if (price.Quantity <= 0m)
                return new DishCost(null, false); // degenerate observation — never fabricate a number

            var unitPrice = price.Price / price.Quantity;

            // Product dishes retain the denomination selected in the plan.  A price observation can
            // be recorded in another unit; convert one observation unit into the planned unit before
            // deriving the per-planned-unit price, then multiply by the stored quantity.
            // Persisted product dishes always carry their own unit snapshot.  A malformed
            // in-memory dish is unpriced rather than silently deriving the current Catalog default.
            var plannedUnitId = dish.UnitId;
            var plannedQuantity = dish.Quantity;
            if (plannedUnitId is not { } savedUnit || savedUnit == Guid.Empty || plannedQuantity is not > 0m)
                return new DishCost(null, false); // product unresolvable — never fabricate a number

            decimal costPerPlannedUnit;
            if (price.UnitId == savedUnit)
            {
                // Common case: the observation is already in the saved planned unit — no conversion, no
                // extra IO round trip.
                costPerPlannedUnit = unitPrice;
            }
            else
            {
                var conversion = await unitConverter.ConvertAsync(
                    dish.ProductId.Value, 1m, price.UnitId, savedUnit, ct);
                if (conversion.IsFailure || conversion.Value <= 0m)
                    // No conversion path — e.g. the planned unit is mass/volume and the observation
                    // was recorded in an incompatible unit, so the saved quantity has no resolvable
                    // magnitude. Never fabricate a number; flag the dish as unpriced instead.
                    return new DishCost(null, false);

                // conversion.Value = how many planned units one observation unit converts to.
                costPerPlannedUnit = unitPrice / conversion.Value;
            }

            var cost = costPerPlannedUnit * plannedQuantity.Value;
            return new DishCost(cost, false);
        }

        return new DishCost(null, false);
    }

    private static MealCost Aggregate(IReadOnlyList<DishCost> dishes)
    {
        if (dishes.Count == 0)
            return MealCost.None;

        decimal total = 0m;
        var anyPriced = false;
        var anyPartial = false;
        var anyUnpriced = false;

        foreach (var d in dishes)
        {
            if (d.Amount.HasValue)
            {
                total += d.Amount.Value;
                anyPriced = true;
                if (d.IsPartial) anyPartial = true;
            }
            else
            {
                anyUnpriced = true;
            }
        }

        if (!anyPriced)
            return MealCost.None;

        var completeness = (anyPriced && anyUnpriced) || anyPartial
            ? CostCompleteness.Partial
            : CostCompleteness.Full;

        return new MealCost(total, completeness);
    }

    private sealed record DishCost(decimal? Amount, bool IsPartial);
}

// ── Value objects ────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Three-state completeness of a cost roll-up (mirrors <c>CostCompleteness</c> in Recipes).
/// </summary>
public enum CostCompleteness
{
    /// <summary>Every dish in the scope has full pricing data.</summary>
    Full,

    /// <summary>Some dishes have pricing data, some do not — the figure is an under-estimate.</summary>
    Partial,

    /// <summary>No dish has pricing data — figure is omitted entirely.</summary>
    None,
}

/// <summary>
/// Rolled-up cost estimate for a planned meal or week (computed read model — never persisted).
/// Deal-aware (P5-9b) — uses Pricing's effective price (cheapest active deal else latest purchase).
/// </summary>
/// <param name="Amount">
/// Total estimated cost, or null when <see cref="Completeness"/> is <see cref="CostCompleteness.None"/>.
/// When <see cref="CostCompleteness.Partial"/> this is a flagged under-estimate.
/// </param>
/// <param name="Completeness">How complete the cost estimate is.</param>
public sealed record MealCost(decimal? Amount, CostCompleteness Completeness)
{
    /// <summary>Sentinel returned for note-meals, empty meals, and meals with no pricing data.</summary>
    public static readonly MealCost None = new(null, CostCompleteness.None);
}
