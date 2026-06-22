using Plantry.SharedKernel;

namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Aggregate root holding a household's default planning settings:
/// an optional weekly budget target and optional default planning weights.
/// One row per household; seeded lazily on first write (null = no target).
/// Null DefaultWeeklyBudget suppresses the over-budget insight (M1).
/// HouseholdId is the PK — one settings record per household.
/// </summary>
public sealed class HouseholdPlanningSettings
{
    // Required by EF
    private HouseholdPlanningSettings() { }

    private HouseholdPlanningSettings(HouseholdId householdId)
    {
        HouseholdId = householdId;
    }

    /// <summary>Primary key — one settings record per household.</summary>
    public HouseholdId HouseholdId { get; private set; }

    /// <summary>Household-default weekly budget. Null = no budget target.</summary>
    public Money? DefaultWeeklyBudget { get; private set; }

    /// <summary>Household-default AI planning weights. Null = use PlanningWeights.Default.</summary>
    public PlanningWeights? DefaultPlanningWeights { get; private set; }

    /// <summary>Creates a new empty settings record for the household (lazy seeding on first write).</summary>
    public static HouseholdPlanningSettings Create(HouseholdId householdId) =>
        new(householdId);

    /// <summary>
    /// Updates the household default budget and/or weights.
    /// Pass null to clear a value.
    /// </summary>
    public void SetDefaults(Money? budget, PlanningWeights? weights)
    {
        DefaultWeeklyBudget = budget;
        DefaultPlanningWeights = weights;
    }
}
