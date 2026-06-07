using Microsoft.EntityFrameworkCore;
using Plantry.Identity.Domain;
using Plantry.SharedKernel;

namespace Plantry.Identity.Infrastructure;

public sealed class HouseholdRepository(PlantryIdentityDbContext db) : IHouseholdRepository
{
    public Task<Household?> FindAsync(HouseholdId id, CancellationToken ct = default) =>
        db.Households.FirstOrDefaultAsync(h => h.Id == id, ct);

    public async Task AddAsync(Household household, CancellationToken ct = default) =>
        await db.Households.AddAsync(household, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
