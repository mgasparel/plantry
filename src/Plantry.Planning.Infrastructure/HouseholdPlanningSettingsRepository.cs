using Microsoft.EntityFrameworkCore;
using Plantry.Planning.Domain;
using Plantry.SharedKernel;

namespace Plantry.Planning.Infrastructure;

public sealed class HouseholdPlanningSettingsRepository(PlanningDbContext db)
    : IHouseholdPlanningSettingsRepository
{
    public Task<HouseholdPlanningSettings?> FindByHouseholdAsync(
        HouseholdId householdId, CancellationToken ct = default) =>
        db.HouseholdPlanningSettings
            .FirstOrDefaultAsync(s => s.HouseholdId == householdId, ct);

    public async Task AddAsync(HouseholdPlanningSettings settings, CancellationToken ct = default) =>
        await db.HouseholdPlanningSettings.AddAsync(settings, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
