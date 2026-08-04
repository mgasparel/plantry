using Plantry.SharedKernel;

namespace Plantry.Planning.Domain;

/// <summary>
/// Repository port for <see cref="MealSlotConfig"/>. Implemented in
/// <c>Plantry.Planning.Infrastructure</c>.
/// </summary>
public interface IMealSlotConfigRepository
{
    Task<MealSlotConfig?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default);
    Task AddAsync(MealSlotConfig config, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
