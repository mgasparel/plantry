using Microsoft.EntityFrameworkCore;
using Plantry.Planning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Planning.Infrastructure;

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

    public async Task<IReadOnlyDictionary<Guid, PlannedMealSlotInfo>> FindSlotLabelsAsync(
        IReadOnlyList<Guid> plannedMealIds, CancellationToken ct = default)
    {
        if (plannedMealIds.Count == 0)
            return new Dictionary<Guid, PlannedMealSlotInfo>();

        // Match on the strongly-typed key: EF cannot translate a .Value access on a converted value-object
        // key combined with the converted-key household query filter (same constraint as the sibling adapters).
        var wanted = plannedMealIds.Select(PlannedMealId.From).ToHashSet();
        var meals = await db.PlannedMeals
            .Where(pm => wanted.Contains(pm.Id))
            .Select(pm => new { pm.Id, pm.Date, pm.MealSlotId })
            .ToListAsync(ct);

        if (meals.Count == 0)
            return new Dictionary<Guid, PlannedMealSlotInfo>();

        // Resolve the label for each referenced slot in one batch. MealSlot is never physically deleted
        // (only soft-archived), so a planned meal's slot always resolves (M10).
        var slotIds = meals.Select(m => m.MealSlotId).ToHashSet();
        var labels = await db.MealSlots
            .Where(ms => slotIds.Contains(ms.Id))
            .Select(ms => new { ms.Id, ms.Label })
            .ToDictionaryAsync(x => x.Id, x => x.Label, ct);

        var result = new Dictionary<Guid, PlannedMealSlotInfo>();
        foreach (var m in meals)
        {
            // Omit an entry whose slot label cannot be resolved so the caller falls back gracefully.
            if (labels.TryGetValue(m.MealSlotId, out var label))
                result[m.Id.Value] = new PlannedMealSlotInfo(m.Date.DayOfWeek, label);
        }

        return result;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
