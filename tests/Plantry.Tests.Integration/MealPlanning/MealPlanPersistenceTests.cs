using Microsoft.EntityFrameworkCore;
using Plantry.Planning.Domain;
using Plantry.Planning.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.MealPlanning;

/// <summary>
/// L3 integration tests: MealPlan aggregate round-trips through the real PostgreSQL schema.
/// Covers persist + reload, RLS isolation, and the dishes/note XOR constraint.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class MealPlanPersistenceTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly DateOnly Monday = new(2026, 6, 1);

    // _slotId is per-instance (not static) so each test gets its own DB-seeded slot.
    private MealSlotId _slotId = MealSlotId.New();

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
        _slotId = MealSlotId.New();

        // Seed a MealSlotConfig with _slotId so the FK constraint
        // fk_planned_meal_slot_composite is satisfied when persisting PlannedMeals.
        await SeedSlotConfigAsync(_household, _slotId);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── helpers ───────────────────────────────────────────────────────────────

    private DbContextOptions<PlanningDbContext> MealPlanningOptions() =>
        new DbContextOptionsBuilder<PlanningDbContext>().UseNpgsql(db.ConnectionString).Options;

    private PlanningDbContext NewDb(HouseholdId household)
    {
        var ctx = new PlanningDbContext(MealPlanningOptions());
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }

    /// <summary>
    /// Seed a MealSlotConfig containing the given slot ID into the database.
    /// Required because planned_meal has a composite FK to meal_slot (household_id, meal_slot_id).
    /// </summary>
    private async Task SeedSlotConfigAsync(HouseholdId household, MealSlotId slotId)
    {
        var configId = Guid.NewGuid();
        await using var seedDb = new PlanningDbContext(MealPlanningOptions());
        // Use raw SQL to bypass EF query filter (which would block writes with Guid.Empty household)
        await seedDb.Database.ExecuteSqlRawAsync(@"
            INSERT INTO meal_planning.meal_slot_config (meal_slot_config_id, household_id, created_at, updated_at)
            VALUES ({0}, {1}, NOW(), NOW());
            INSERT INTO meal_planning.meal_slot (meal_slot_id, household_id, meal_slot_config_id, label, ordinal, default_attendees)
            VALUES ({2}, {1}, {0}, 'Test Slot', 1, '{{}}');",
            configId, household.Value, slotId.Value);
    }

    private static DishSpec RecipeDish() => new(DishKind.Recipe, Guid.NewGuid(), 2);
    private static DishSpec RecipeDish(Guid recipeId) => new(DishKind.Recipe, recipeId, 2);
    private static readonly Guid UserId = Guid.NewGuid();

    // ── write + read round-trips ──────────────────────────────────────────────

    [Fact(DisplayName = "AssignMeal: plan + dish persists and reloads correctly")]
    public async Task AssignMeal_Persists_AndReloads()
    {
        MealPlanId planId;
        PlannedMealId mealId;

        await using (var writeDb = NewDb(_household))
        {
            var plan = MealPlan.Start(_household, Monday, Clock);
            planId = plan.Id;
            plan.AssignMeal(Monday, _slotId, [RecipeDish(), RecipeDish()], null, "manual", UserId, Clock);
            mealId = plan.PlannedMeals[0].Id;
            writeDb.MealPlans.Add(plan);
            await writeDb.SaveChangesAsync();
        }

        await using var readDb = NewDb(_household);
        var loaded = await readDb.MealPlans
            .Include(mp => mp.PlannedMeals).ThenInclude(pm => pm.PlannedDishes)
            .SingleAsync(mp => mp.Id == planId);

        Assert.Equal(Monday, loaded.WeekStart);
        var meal = Assert.Single(loaded.PlannedMeals);
        Assert.Equal(mealId, meal.Id);
        Assert.Equal(Monday, meal.Date);
        Assert.Equal(_slotId, meal.MealSlotId);
        Assert.Null(meal.Note);
        Assert.Equal(2, meal.PlannedDishes.Count);
        Assert.Equal("manual", meal.Source);
    }

    [Fact(DisplayName = "AssignNote: note-based meal persists and reloads correctly")]
    public async Task AssignNote_Persists_AndReloads()
    {
        MealPlanId planId;

        await using (var writeDb = NewDb(_household))
        {
            var plan = MealPlan.Start(_household, Monday, Clock);
            planId = plan.Id;
            plan.AssignNote(Monday, _slotId, "Takeout night", null, "manual", UserId, Clock);
            writeDb.MealPlans.Add(plan);
            await writeDb.SaveChangesAsync();
        }

        await using var readDb = NewDb(_household);
        var loaded = await readDb.MealPlans
            .Include(mp => mp.PlannedMeals).ThenInclude(pm => pm.PlannedDishes)
            .SingleAsync(mp => mp.Id == planId);

        var meal = Assert.Single(loaded.PlannedMeals);
        Assert.Equal("Takeout night", meal.Note);
        Assert.Empty(meal.PlannedDishes);
    }

    [Fact(DisplayName = "WeekStart is always a Monday (M8) after persist")]
    public async Task WeekStart_IsAlwaysMonday()
    {
        var wednesday = Monday.AddDays(2);

        await using (var writeDb = NewDb(_household))
        {
            var plan = MealPlan.Start(_household, wednesday, Clock); // normalizes to Monday
            writeDb.MealPlans.Add(plan);
            await writeDb.SaveChangesAsync();
        }

        await using var readDb = NewDb(_household);
        var loaded = await readDb.MealPlans.SingleAsync();
        Assert.Equal(DayOfWeek.Monday, loaded.WeekStart.DayOfWeek);
        Assert.Equal(Monday, loaded.WeekStart);
    }

    [Fact(DisplayName = "AttendeesOverride persists and reloads as non-null list")]
    public async Task AttendeesOverride_Persists()
    {
        var memberA = Guid.NewGuid();
        var memberB = Guid.NewGuid();
        MealPlanId planId;

        await using (var writeDb = NewDb(_household))
        {
            var plan = MealPlan.Start(_household, Monday, Clock);
            planId = plan.Id;
            plan.AssignMeal(Monday, _slotId, [RecipeDish()], [memberA, memberB], "manual", UserId, Clock);
            writeDb.MealPlans.Add(plan);
            await writeDb.SaveChangesAsync();
        }

        await using var readDb = NewDb(_household);
        var loaded = await readDb.MealPlans
            .Include(mp => mp.PlannedMeals)
            .SingleAsync(mp => mp.Id == planId);

        var meal = Assert.Single(loaded.PlannedMeals);
        Assert.NotNull(meal.AttendeesOverride);
        Assert.Contains(memberA, meal.AttendeesOverride);
        Assert.Contains(memberB, meal.AttendeesOverride);
    }

    // ── RLS isolation ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "RLS: household A cannot see household B's meals")]
    public async Task MealPlan_RlsIsolation()
    {
        var householdA = HouseholdId.New();
        var householdB = HouseholdId.New();
        var slotA = MealSlotId.New();
        var slotB = MealSlotId.New();

        // Seed slot configs for each household before inserting meals
        await SeedSlotConfigAsync(householdA, slotA);
        await SeedSlotConfigAsync(householdB, slotB);

        await using (var writeA = NewDb(householdA))
        {
            var plan = MealPlan.Start(householdA, Monday, Clock);
            plan.AssignMeal(Monday, slotA, [RecipeDish()], null, "manual", UserId, Clock);
            writeA.MealPlans.Add(plan);
            await writeA.SaveChangesAsync();
        }

        await using (var writeB = NewDb(householdB))
        {
            var plan = MealPlan.Start(householdB, Monday, Clock);
            plan.AssignNote(Monday, slotB, "B's note", null, "manual", UserId, Clock);
            writeB.MealPlans.Add(plan);
            await writeB.SaveChangesAsync();
        }

        // Household A should only see its own plan
        await using var readA = NewDb(householdA);
        var plansA = await readA.MealPlans.Include(mp => mp.PlannedMeals).ToListAsync();
        Assert.Single(plansA);
        Assert.All(plansA[0].PlannedMeals, m => Assert.Null(m.Note));

        // Household B should only see its own plan
        await using var readB = NewDb(householdB);
        var plansB = await readB.MealPlans.Include(mp => mp.PlannedMeals).ToListAsync();
        Assert.Single(plansB);
        Assert.All(plansB[0].PlannedMeals, m => Assert.Equal("B's note", m.Note));
    }

    [Fact(DisplayName = "Unique constraint: one plan per (household, week_start)")]
    public async Task MealPlan_UniqueConstraint_OnePerHouseholdWeek()
    {
        await using (var writeDb = NewDb(_household))
        {
            var plan1 = MealPlan.Start(_household, Monday, Clock);
            writeDb.MealPlans.Add(plan1);
            await writeDb.SaveChangesAsync();
        }

        await using var writeDb2 = NewDb(_household);
        var plan2 = MealPlan.Start(_household, Monday, Clock); // same week
        writeDb2.MealPlans.Add(plan2);

        await Assert.ThrowsAsync<DbUpdateException>(() => writeDb2.SaveChangesAsync());
    }

    // ── MP-O8: cell stack ─────────────────────────────────────────────────────

    [Fact(DisplayName = "MP-O8: two meals persist in one cell with ordinals 1 and 2")]
    public async Task TwoMealsInCell_Persist_WithContiguousOrdinals()
    {
        MealPlanId planId;

        await using (var writeDb = NewDb(_household))
        {
            var plan = MealPlan.Start(_household, Monday, Clock);
            planId = plan.Id;
            plan.AssignNote(Monday, _slotId, "Breakfast A", null, "manual", UserId, Clock);
            plan.AssignNote(Monday, _slotId, "Breakfast B", null, "manual", UserId, Clock);
            writeDb.MealPlans.Add(plan);
            await writeDb.SaveChangesAsync();
        }

        await using var readDb = NewDb(_household);
        var loaded = await readDb.MealPlans
            .Include(mp => mp.PlannedMeals)
            .SingleAsync(mp => mp.Id == planId);

        var cell = loaded.MealsInCell(Monday, _slotId);
        Assert.Equal(2, cell.Count);
        Assert.Equal(1, cell[0].Ordinal);
        Assert.Equal(2, cell[1].Ordinal);
        Assert.Equal("Breakfast A", cell[0].Note);
        Assert.Equal("Breakfast B", cell[1].Note);
    }

    // ── UpdateDishes — diff instead of replace (plantry-0tnx) ─────────────────
    // These exercise the actual persistence boundary: UpdateDishes now emits in-place UPDATEs of
    // planned_dish.ordinal for kept dishes instead of delete-all + insert-all, which is where the
    // ux_planned_dish_meal_ordinal unique index risk lives (see PlannedMeal.UpdateDishes doc).

    [Fact(DisplayName = "UpdateDishes: kept dish's id survives an edit that replaces another dish")]
    public async Task UpdateDishes_PreservesKeptDishId_AcrossReload()
    {
        var recipeA = Guid.NewGuid();
        var recipeB = Guid.NewGuid();
        MealPlanId planId;
        PlannedMealId mealId;
        Guid keptDishId;

        await using (var writeDb = NewDb(_household))
        {
            var plan = MealPlan.Start(_household, Monday, Clock);
            planId = plan.Id;
            plan.AssignMeal(Monday, _slotId, [RecipeDish(recipeA), RecipeDish(recipeB)], null, "manual", UserId, Clock);
            mealId = plan.PlannedMeals[0].Id;
            keptDishId = plan.PlannedMeals[0].PlannedDishes.Single(d => d.RecipeId == recipeA).Id.Value;
            writeDb.MealPlans.Add(plan);
            await writeDb.SaveChangesAsync();
        }

        var recipeC = Guid.NewGuid();
        await using (var editDb = NewDb(_household))
        {
            var plan = await editDb.MealPlans
                .Include(mp => mp.PlannedMeals).ThenInclude(pm => pm.PlannedDishes)
                .SingleAsync(mp => mp.Id == planId);
            plan.AssignMeal(Monday, _slotId, [RecipeDish(recipeA), RecipeDish(recipeC)], null, "manual", UserId, Clock, mealId: mealId);
            await editDb.SaveChangesAsync();
        }

        await using var readDb = NewDb(_household);
        var loaded = await readDb.MealPlans
            .Include(mp => mp.PlannedMeals).ThenInclude(pm => pm.PlannedDishes)
            .SingleAsync(mp => mp.Id == planId);
        var meal = Assert.Single(loaded.PlannedMeals);

        Assert.Equal(2, meal.PlannedDishes.Count);
        var keptDish = Assert.Single(meal.PlannedDishes, d => d.RecipeId == recipeA);
        Assert.Equal(keptDishId, keptDish.Id.Value);
        Assert.DoesNotContain(meal.PlannedDishes, d => d.RecipeId == recipeB);
        Assert.Contains(meal.PlannedDishes, d => d.RecipeId == recipeC);
    }

    [Fact(DisplayName = "UpdateDishes: removing the leading dish shifts the kept dish's ordinal down and saves")]
    public async Task UpdateDishes_RemovingLeadingDish_ShiftsKeptDishOrdinalDown()
    {
        var recipeA = Guid.NewGuid();
        var recipeB = Guid.NewGuid();
        MealPlanId planId;
        PlannedMealId mealId;

        await using (var writeDb = NewDb(_household))
        {
            var plan = MealPlan.Start(_household, Monday, Clock);
            planId = plan.Id;
            plan.AssignMeal(Monday, _slotId, [RecipeDish(recipeA), RecipeDish(recipeB)], null, "manual", UserId, Clock);
            mealId = plan.PlannedMeals[0].Id;
            writeDb.MealPlans.Add(plan);
            await writeDb.SaveChangesAsync();
        }

        await using (var editDb = NewDb(_household))
        {
            var plan = await editDb.MealPlans
                .Include(mp => mp.PlannedMeals).ThenInclude(pm => pm.PlannedDishes)
                .SingleAsync(mp => mp.Id == planId);
            // Drop A (ordinal 1), keep only B — B must move from ordinal 2 to ordinal 1 while its
            // row (still holding ordinal 2) is the only remaining row.
            plan.AssignMeal(Monday, _slotId, [RecipeDish(recipeB)], null, "manual", UserId, Clock, mealId: mealId);
            await editDb.SaveChangesAsync();
        }

        await using var readDb = NewDb(_household);
        var loaded = await readDb.MealPlans
            .Include(mp => mp.PlannedMeals).ThenInclude(pm => pm.PlannedDishes)
            .SingleAsync(mp => mp.Id == planId);
        var meal = Assert.Single(loaded.PlannedMeals);
        var dish = Assert.Single(meal.PlannedDishes);

        Assert.Equal(recipeB, dish.RecipeId);
        Assert.Equal(1, dish.Ordinal);
    }

    [Fact(DisplayName = "UpdateDishes: remove-then-re-add spec order does not collide on the ordinal unique index")]
    public async Task UpdateDishes_RemoveThenReAddSpecOrder_DoesNotCollideOnOrdinalUniqueIndex()
    {
        var recipeA = Guid.NewGuid();
        var recipeB = Guid.NewGuid();
        MealPlanId planId;
        PlannedMealId mealId;

        await using (var writeDb = NewDb(_household))
        {
            var plan = MealPlan.Start(_household, Monday, Clock);
            planId = plan.Id;
            plan.AssignMeal(Monday, _slotId, [RecipeDish(recipeA), RecipeDish(recipeB)], null, "manual", UserId, Clock);
            mealId = plan.PlannedMeals[0].Id;
            writeDb.MealPlans.Add(plan);
            await writeDb.SaveChangesAsync();
        }

        await using (var editDb = NewDb(_household))
        {
            var plan = await editDb.MealPlans
                .Include(mp => mp.PlannedMeals).ThenInclude(pm => pm.PlannedDishes)
                .SingleAsync(mp => mp.Id == planId);
            // Spec order [B, A] mirrors a user removing A (ordinal 1) then re-adding it: the
            // editor round-trips the remaining dishes first, so B leads. Without the monotonic
            // match constraint this would try to swap A and B's ordinals in one SaveChanges and
            // collide against the non-deferrable ux_planned_dish_meal_ordinal unique index.
            plan.AssignMeal(Monday, _slotId, [RecipeDish(recipeB), RecipeDish(recipeA)], null, "manual", UserId, Clock, mealId: mealId);

            // Should not throw.
            await editDb.SaveChangesAsync();
        }

        await using var readDb = NewDb(_household);
        var loaded = await readDb.MealPlans
            .Include(mp => mp.PlannedMeals).ThenInclude(pm => pm.PlannedDishes)
            .SingleAsync(mp => mp.Id == planId);
        var meal = Assert.Single(loaded.PlannedMeals);

        Assert.Equal(2, meal.PlannedDishes.Count);
        Assert.Contains(meal.PlannedDishes, d => d.RecipeId == recipeA);
        Assert.Contains(meal.PlannedDishes, d => d.RecipeId == recipeB);
    }

    [Fact(DisplayName = "MP-O8: duplicate (plan, date, slot, ordinal) is rejected by the unique index")]
    public async Task DuplicateOrdinal_InSameCell_IsRejectedByDb()
    {
        MealPlanId planId;

        // Write a plan with one note meal (ordinal 1) first
        await using (var writeDb = NewDb(_household))
        {
            var plan = MealPlan.Start(_household, Monday, Clock);
            planId = plan.Id;
            plan.AssignNote(Monday, _slotId, "First", null, "manual", UserId, Clock);
            writeDb.MealPlans.Add(plan);
            await writeDb.SaveChangesAsync();
        }

        // Attempt to insert a second row with the same (meal_plan_id, date, meal_slot_id, ordinal=1)
        // directly via raw SQL to prove the DB-level unique index ux_planned_meal_plan_date_slot_ordinal
        // enforces uniqueness at the persistence layer.
        await using var rawDb = new PlanningDbContext(MealPlanningOptions());
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            rawDb.Database.ExecuteSqlRawAsync(@"
                INSERT INTO meal_planning.planned_meal
                    (planned_meal_id, household_id, meal_plan_id, date, meal_slot_id,
                     note, dishes, source, created_by, created_at, updated_by, updated_at,
                     attendees_override, ordinal)
                VALUES
                    (gen_random_uuid(), {0}, {1}, {2}, {3},
                     'Duplicate', '[]'::jsonb, 'test', {4}, NOW(), {4}, NOW(),
                     NULL, 1)",
                _household.Value, planId.Value, Monday, _slotId.Value, UserId));
    }
}
