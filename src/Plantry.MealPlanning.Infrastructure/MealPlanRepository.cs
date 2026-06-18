using Microsoft.EntityFrameworkCore;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.MealPlanning.Infrastructure;

/// <summary>
/// EF-backed repository for the <see cref="MealPlan"/> aggregate.
/// </summary>
public sealed class MealPlanRepository(MealPlanningDbContext db) : IMealPlanRepository
{
    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default) =>
        db.MealPlans
            .Include(mp => mp.PlannedMeals)
                .ThenInclude(pm => pm.PlannedDishes)
            .FirstOrDefaultAsync(mp => mp.HouseholdId == householdId && mp.WeekStart == weekStart, ct);

    public async Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default)
    {
        var existing = await FindByWeekAsync(householdId, weekStart, ct);
        if (existing is not null) return existing;

        var newPlan = MealPlan.Start(householdId, weekStart, clock);
        await db.MealPlans.AddAsync(newPlan, ct);
        return newPlan;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
