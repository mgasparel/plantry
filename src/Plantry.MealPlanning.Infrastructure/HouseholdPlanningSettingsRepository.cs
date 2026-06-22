using Microsoft.EntityFrameworkCore;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;

namespace Plantry.MealPlanning.Infrastructure;

public sealed class HouseholdPlanningSettingsRepository(MealPlanningDbContext db)
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
