using Plantry.SharedKernel;

namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Repository port for <see cref="WeekPlanningOverride"/>.
/// Implemented in <c>Plantry.MealPlanning.Infrastructure</c>.
/// </summary>
public interface IWeekPlanningOverrideRepository
{
    /// <summary>Returns the override for the given household and week start, or null if none exists.</summary>
    Task<WeekPlanningOverride?> FindAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default);

    Task AddAsync(WeekPlanningOverride weekOverride, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
