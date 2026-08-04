using Microsoft.EntityFrameworkCore;
using Plantry.Planning.Domain;
using Plantry.SharedKernel;

namespace Plantry.Planning.Infrastructure;

public sealed class MealSlotConfigRepository(MealPlanningDbContext db) : IMealSlotConfigRepository
{
    public Task<MealSlotConfig?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        db.MealSlotConfigs
            .Include(c => c.Slots)
            .FirstOrDefaultAsync(c => c.HouseholdId == householdId, ct);

    public async Task AddAsync(MealSlotConfig config, CancellationToken ct = default) =>
        await db.MealSlotConfigs.AddAsync(config, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
