using Plantry.SharedKernel;

namespace Plantry.Identity.Domain;

public interface IHouseholdRepository
{
    Task<Household?> FindAsync(HouseholdId id, CancellationToken ct = default);
    Task AddAsync(Household household, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
