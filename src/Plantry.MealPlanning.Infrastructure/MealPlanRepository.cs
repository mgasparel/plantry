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

    /// <inheritdoc />
    public async Task SwapMealPositionsAsync(
        PlannedMealId mealAId, DateOnly newDateA, MealSlotId newSlotA,
        PlannedMealId mealBId, DateOnly newDateB, MealSlotId newSlotB,
        Guid updatedBy, DateTimeOffset now,
        CancellationToken ct = default)
    {
        // EF Core detects a circular dependency when two tracked PlannedMeal rows swap
        // positions on the unique index (meal_plan_id, date, meal_slot_id): updating row A
        // to the old position of row B conflicts with row B's still-in-place row, and vice
        // versa. EF throws InvalidOperationException before issuing any SQL.
        //
        // We work around this by issuing two raw SQL UPDATEs in one explicit transaction,
        // then detaching both entities so EF's change tracker does not issue duplicate
        // UPDATEs on the next SaveChangesAsync call.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Pass parameters as an explicit array to avoid CancellationToken being
        // mis-interpreted as a SQL parameter by the params object[] overload.
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE meal_planning.planned_meal " +
            "SET date = {0}, meal_slot_id = {1}, updated_by = {2}, updated_at = {3} " +
            "WHERE planned_meal_id = {4}",
            parameters: new object[] { newDateA, newSlotA.Value, updatedBy, now, mealAId.Value });
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE meal_planning.planned_meal " +
            "SET date = {0}, meal_slot_id = {1}, updated_by = {2}, updated_at = {3} " +
            "WHERE planned_meal_id = {4}",
            parameters: new object[] { newDateB, newSlotB.Value, updatedBy, now, mealBId.Value });
        await tx.CommitAsync(ct);

        // Detach the two rows from EF's change tracker so the caller's SaveChangesAsync
        // (if called afterwards) does not re-issue conflicting UPDATEs for these entities.
        var trackedA = db.ChangeTracker.Entries<PlannedMeal>()
            .FirstOrDefault(e => e.Entity.Id == mealAId);
        var trackedB = db.ChangeTracker.Entries<PlannedMeal>()
            .FirstOrDefault(e => e.Entity.Id == mealBId);
        if (trackedA != null) trackedA.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        if (trackedB != null) trackedB.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
    }
}
