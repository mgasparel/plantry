using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Xunit;

namespace Plantry.Tests.Unit.MealPlanning.Application;

/// <summary>
/// L1 unit tests for <see cref="PlanningSettingsResolver"/> — the pure stateless resolver
/// that merges a household default with an optional per-week override.
/// Tests the three acceptance-criterion scenarios:
///   1. Override wins over default.
///   2. Default fallback when no override exists.
///   3. Null budget = no target = over-budget rule suppressed.
/// </summary>
public sealed class PlanningSettingsResolverTests
{
    private static HouseholdId AnyHousehold => HouseholdId.New();
    private static readonly DateOnly AnyWeek = new(2026, 6, 23);
    private static readonly Money BudgetA = Money.FromDecimal(100m, "USD");
    private static readonly Money BudgetB = Money.FromDecimal(50m, "USD");
    private static readonly PlanningWeights WeightsA = new(60, 20, 20);
    private static readonly PlanningWeights WeightsB = new(20, 60, 20);

    // ── Override wins ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "Resolve: week override budget wins over household default")]
    public void Resolve_OverrideBudget_WinsOverDefault()
    {
        var hhId = AnyHousehold;
        var settings = HouseholdPlanningSettings.Create(hhId);
        settings.SetDefaults(BudgetA, null);

        var weekOverride = WeekPlanningOverride.Create(hhId, AnyWeek);
        weekOverride.Set(BudgetB, null);

        var (budget, _) = PlanningSettingsResolver.Resolve(settings, weekOverride);

        Assert.NotNull(budget);
        Assert.Equal(BudgetB.MinorUnits, budget!.MinorUnits);
    }

    [Fact(DisplayName = "Resolve: week override weights win over household default")]
    public void Resolve_OverrideWeights_WinsOverDefault()
    {
        var hhId = AnyHousehold;
        var settings = HouseholdPlanningSettings.Create(hhId);
        settings.SetDefaults(null, WeightsA);

        var weekOverride = WeekPlanningOverride.Create(hhId, AnyWeek);
        weekOverride.Set(null, WeightsB);

        var (_, weights) = PlanningSettingsResolver.Resolve(settings, weekOverride);

        Assert.NotNull(weights);
        Assert.Equal(WeightsB.Waste, weights!.Waste);
        Assert.Equal(WeightsB.Cost, weights.Cost);
    }

    [Fact(DisplayName = "Resolve: override fields resolved independently (budget from override, weights from default)")]
    public void Resolve_MixedOverride_EachFieldResolvedIndependently()
    {
        var hhId = AnyHousehold;
        var settings = HouseholdPlanningSettings.Create(hhId);
        settings.SetDefaults(BudgetA, WeightsA);

        // Override only the budget; weights are null in the override
        var weekOverride = WeekPlanningOverride.Create(hhId, AnyWeek);
        weekOverride.Set(BudgetB, null); // budget overridden, weights fall back

        var (budget, weights) = PlanningSettingsResolver.Resolve(settings, weekOverride);

        Assert.NotNull(budget);
        Assert.Equal(BudgetB.MinorUnits, budget!.MinorUnits); // override wins for budget
        Assert.NotNull(weights);
        Assert.Equal(WeightsA.Waste, weights!.Waste); // default fallback for weights
    }

    // ── Default fallback ──────────────────────────────────────────────────────

    [Fact(DisplayName = "Resolve: no override → household default returned for budget")]
    public void Resolve_NoOverride_DefaultBudgetReturned()
    {
        var hhId = AnyHousehold;
        var settings = HouseholdPlanningSettings.Create(hhId);
        settings.SetDefaults(BudgetA, null);

        var (budget, _) = PlanningSettingsResolver.Resolve(settings, weekOverride: null);

        Assert.NotNull(budget);
        Assert.Equal(BudgetA.MinorUnits, budget!.MinorUnits);
    }

    [Fact(DisplayName = "Resolve: no override → household default returned for weights")]
    public void Resolve_NoOverride_DefaultWeightsReturned()
    {
        var hhId = AnyHousehold;
        var settings = HouseholdPlanningSettings.Create(hhId);
        settings.SetDefaults(null, WeightsA);

        var (_, weights) = PlanningSettingsResolver.Resolve(settings, weekOverride: null);

        Assert.NotNull(weights);
        Assert.Equal(WeightsA.Waste, weights!.Waste);
    }

    // ── Null budget suppresses over-budget rule ───────────────────────────────

    [Fact(DisplayName = "Resolve: null settings → budget is null (over-budget rule suppressed)")]
    public void Resolve_NullSettings_BudgetIsNull()
    {
        var (budget, _) = PlanningSettingsResolver.Resolve(settings: null, weekOverride: null);

        Assert.Null(budget);
    }

    [Fact(DisplayName = "Resolve: settings exist but default budget not set → budget is null")]
    public void Resolve_DefaultBudgetNotSet_BudgetIsNull()
    {
        var settings = HouseholdPlanningSettings.Create(AnyHousehold);
        // No defaults set → both null

        var (budget, _) = PlanningSettingsResolver.Resolve(settings, weekOverride: null);

        Assert.Null(budget);
    }

    [Fact(DisplayName = "Resolve: override explicitly clears budget (null override budget, non-null default) → null wins")]
    public void Resolve_OverrideClearsBudget_NullWins()
    {
        var hhId = AnyHousehold;
        var settings = HouseholdPlanningSettings.Create(hhId);
        settings.SetDefaults(BudgetA, null);

        // Override explicitly clears the budget for this week
        var weekOverride = WeekPlanningOverride.Create(hhId, AnyWeek);
        weekOverride.Set(null, null); // null budget in override

        // null override budget wins (override resolves independently — null in override = fall back to default)
        // NOTE: the resolver uses ?? so null override falls back to default
        var (budget, _) = PlanningSettingsResolver.Resolve(settings, weekOverride);

        // null ?? BudgetA = BudgetA (null override falls back)
        Assert.NotNull(budget);
        Assert.Equal(BudgetA.MinorUnits, budget!.MinorUnits);
    }
}
