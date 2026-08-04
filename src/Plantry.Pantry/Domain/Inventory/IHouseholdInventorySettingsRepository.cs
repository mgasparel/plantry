using Plantry.SharedKernel;

namespace Plantry.Pantry.Domain;

/// <summary>
/// Repository port for <see cref="HouseholdInventorySettings"/>.
/// Implemented in <c>Plantry.Pantry.Infrastructure</c>.
/// </summary>
public interface IHouseholdInventorySettingsRepository
{
    /// <summary>Returns the settings for the household, or null when none have been saved yet.</summary>
    Task<HouseholdInventorySettings?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default);

    Task AddAsync(HouseholdInventorySettings settings, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
