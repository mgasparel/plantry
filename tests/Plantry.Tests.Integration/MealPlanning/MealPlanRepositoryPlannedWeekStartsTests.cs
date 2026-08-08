using Microsoft.EntityFrameworkCore;
using Plantry.Planning.Domain;
using Plantry.Planning.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.MealPlanning;

/// <summary>
/// L3 integration tests for <see cref="MealPlanRepository.PlannedWeekStartsBeforeAsync"/> (plantry-h9z9)
/// — the single scalar-only query <see cref="Plantry.Planning.Application.MealPlanStreakQuery"/> uses
/// instead of one full-aggregate load per week. Proves, against a real Postgres schema, that it: returns
/// only weeks with at least one planned meal, excludes weeks after the cutoff, orders descending, respects
/// the <c>maxWeeks</c> cap, and is scoped to the signed-in household by the <c>MealPlanningDbContext</c>
/// RLS query filter.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class MealPlanRepositoryPlannedWeekStartsTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly DateOnly Week0 = new(2026, 6, 1); // a known Monday — the "current" week
    private static readonly Guid UserId = Guid.NewGuid();
    private MealSlotId _slotId = MealSlotId.New();

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
        _slotId = MealSlotId.New();
        await SeedSlotConfigAsync(_household, _slotId);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private DbContextOptions<MealPlanningDbContext> Options() =>
        new DbContextOptionsBuilder<MealPlanningDbContext>().UseNpgsql(db.ConnectionString).Options;

    private MealPlanningDbContext NewDb(HouseholdId household)
    {
        var ctx = new MealPlanningDbContext(Options());
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }

    private async Task SeedSlotConfigAsync(HouseholdId household, MealSlotId slotId)
    {
        var configId = Guid.NewGuid();
        await using var seedDb = new MealPlanningDbContext(Options());
        await seedDb.Database.ExecuteSqlRawAsync(@"
            INSERT INTO meal_planning.meal_slot_config (meal_slot_config_id, household_id, created_at, updated_at)
            VALUES ({0}, {1}, NOW(), NOW());
            INSERT INTO meal_planning.meal_slot (meal_slot_id, household_id, meal_slot_config_id, label, ordinal, default_attendees)
            VALUES ({2}, {1}, {0}, 'Test Slot', 1, '{{}}');",
            configId, household.Value, slotId.Value);
    }

    private static DishSpec RecipeDish() => new(DishKind.Recipe, Guid.NewGuid(), 2);

    private async Task SeedPlannedWeekAsync(HouseholdId household, MealSlotId slotId, DateOnly weekStart)
    {
        await using var writeDb = NewDb(household);
        var plan = MealPlan.Start(household, weekStart, Clock);
        plan.AssignMeal(weekStart, slotId, [RecipeDish()], null, "manual", UserId, Clock);
        writeDb.MealPlans.Add(plan);
        await writeDb.SaveChangesAsync();
    }

    /// <summary>Seeds an empty (unplanned) MealPlan aggregate for the week — proves the query filters on
    /// <c>PlannedMeals.Any()</c>, not mere row existence.</summary>
    private async Task SeedEmptyWeekAsync(HouseholdId household, DateOnly weekStart)
    {
        await using var writeDb = NewDb(household);
        var plan = MealPlan.Start(household, weekStart, Clock);
        writeDb.MealPlans.Add(plan);
        await writeDb.SaveChangesAsync();
    }

    [Fact(DisplayName = "Returns only weeks with >=1 planned meal, descending, excluding an empty aggregate")]
    public async Task ReturnsOnlyPlannedWeeks_Descending()
    {
        var week1 = Week0.AddDays(-7);
        var week2 = Week0.AddDays(-14);

        await SeedPlannedWeekAsync(_household, _slotId, Week0);
        await SeedEmptyWeekAsync(_household, week1); // aggregate exists but has zero planned meals
        await SeedPlannedWeekAsync(_household, _slotId, week2);

        await using var readDb = NewDb(_household);
        var repo = new MealPlanRepository(readDb);

        var result = await repo.PlannedWeekStartsBeforeAsync(_household, Week0, maxWeeks: 10);

        Assert.Equal([Week0, week2], result); // week1 (empty aggregate) is excluded
    }

    [Fact(DisplayName = "Excludes weeks after the notAfter cutoff")]
    public async Task ExcludesWeeksAfterCutoff()
    {
        var futureWeek = Week0.AddDays(7);
        await SeedPlannedWeekAsync(_household, _slotId, Week0);
        await SeedPlannedWeekAsync(_household, _slotId, futureWeek);

        await using var readDb = NewDb(_household);
        var repo = new MealPlanRepository(readDb);

        var result = await repo.PlannedWeekStartsBeforeAsync(_household, Week0, maxWeeks: 10);

        Assert.Equal([Week0], result); // futureWeek is after the cutoff
    }

    [Fact(DisplayName = "Respects the maxWeeks cap")]
    public async Task RespectsMaxWeeksCap()
    {
        for (var i = 0; i < 5; i++)
            await SeedPlannedWeekAsync(_household, _slotId, Week0.AddDays(-7 * i));

        await using var readDb = NewDb(_household);
        var repo = new MealPlanRepository(readDb);

        var result = await repo.PlannedWeekStartsBeforeAsync(_household, Week0, maxWeeks: 3);

        Assert.Equal(3, result.Count);
        Assert.Equal([Week0, Week0.AddDays(-7), Week0.AddDays(-14)], result);
    }

    [Fact(DisplayName = "RLS: another household's planned weeks are invisible")]
    public async Task IsScopedToTheHousehold()
    {
        var otherHousehold = HouseholdId.New();
        var otherSlot = MealSlotId.New();
        await SeedSlotConfigAsync(otherHousehold, otherSlot);

        await SeedPlannedWeekAsync(_household, _slotId, Week0);
        await SeedPlannedWeekAsync(otherHousehold, otherSlot, Week0);
        await SeedPlannedWeekAsync(otherHousehold, otherSlot, Week0.AddDays(-7));

        await using var readDb = NewDb(_household);
        var repo = new MealPlanRepository(readDb);

        var result = await repo.PlannedWeekStartsBeforeAsync(_household, Week0, maxWeeks: 10);

        Assert.Equal([Week0], result); // only mine — the other household's two weeks are filtered out
    }
}
