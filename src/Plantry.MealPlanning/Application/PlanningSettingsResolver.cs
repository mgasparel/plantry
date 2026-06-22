using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;

namespace Plantry.MealPlanning.Application;

/// <summary>
/// Pure stateless helper: resolves the effective budget and weights for a given week
/// by merging the household default with the per-week override (override wins).
/// </summary>
public static class PlanningSettingsResolver
{
    /// <summary>
    /// Resolves the effective planning settings for a week.
    /// <para>
    /// Resolution order (each field resolved independently):
    ///   1. Week override (if a row exists and the field is non-null) wins.
    ///   2. Household default (if non-null) is the fallback.
    ///   3. Null = no target (over-budget insight suppressed; AI uses PlanningWeights.Default).
    /// </para>
    /// </summary>
    public static (Money? Budget, PlanningWeights? Weights) Resolve(
        HouseholdPlanningSettings? settings,
        WeekPlanningOverride? weekOverride)
    {
        var budget = weekOverride?.BudgetOverride ?? settings?.DefaultWeeklyBudget;
        var weights = weekOverride?.WeightsOverride ?? settings?.DefaultPlanningWeights;
        return (budget, weights);
    }
}
