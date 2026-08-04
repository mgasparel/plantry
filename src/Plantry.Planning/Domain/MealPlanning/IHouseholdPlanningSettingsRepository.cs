using Plantry.SharedKernel;

namespace Plantry.Planning.Domain;

/// <summary>
/// Repository port for <see cref="HouseholdPlanningSettings"/>.
/// Implemented in <c>Plantry.Planning.Infrastructure</c>.
/// </summary>
public interface IHouseholdPlanningSettingsRepository
{
    /// <summary>Returns the settings for the household, or null if none exist yet.</summary>
    Task<HouseholdPlanningSettings?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default);

    Task AddAsync(HouseholdPlanningSettings settings, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
