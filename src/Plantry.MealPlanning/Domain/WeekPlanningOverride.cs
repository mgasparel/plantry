using Plantry.SharedKernel;

namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Per-(household, weekStart) override for planning settings.
/// A row exists only when the user has overridden something for that specific week.
/// Both fields are nullable — a row can carry a budget-only override, weights-only, or both.
/// </summary>
public sealed class WeekPlanningOverride
{
    // Required by EF
    private WeekPlanningOverride() { }

    private WeekPlanningOverride(HouseholdId householdId, DateOnly weekStart)
    {
        HouseholdId = householdId;
        WeekStart = weekStart;
    }

    /// <summary>Part of composite PK.</summary>
    public HouseholdId HouseholdId { get; private set; }

    /// <summary>Monday of the week this override applies to. Part of composite PK.</summary>
    public DateOnly WeekStart { get; private set; }

    /// <summary>Week-specific budget override. Null = fall back to household default.</summary>
    public Money? BudgetOverride { get; private set; }

    /// <summary>Week-specific weights override. Null = fall back to household default.</summary>
    public PlanningWeights? WeightsOverride { get; private set; }

    /// <summary>Creates a new override row for the given household and week.</summary>
    public static WeekPlanningOverride Create(HouseholdId householdId, DateOnly weekStart) =>
        new(householdId, weekStart);

    /// <summary>Updates the overridden values. Pass null to clear a field.</summary>
    public void Set(Money? budget, PlanningWeights? weights)
    {
        BudgetOverride = budget;
        WeightsOverride = weights;
    }
}
