using Microsoft.EntityFrameworkCore;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;

namespace Plantry.MealPlanning.Infrastructure;

public sealed class WeekPlanningOverrideRepository(MealPlanningDbContext db)
    : IWeekPlanningOverrideRepository
{
    public Task<WeekPlanningOverride?> FindAsync(
        HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default) =>
        db.WeekPlanningOverrides
            .FirstOrDefaultAsync(o => o.HouseholdId == householdId && o.WeekStart == weekStart, ct);

    public async Task AddAsync(WeekPlanningOverride weekOverride, CancellationToken ct = default) =>
        await db.WeekPlanningOverrides.AddAsync(weekOverride, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
