using Microsoft.EntityFrameworkCore;
using Plantry.MealPlanning.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.MealPlanning;

/// <summary>
/// L3 integration tests for the MealPlanning reference-data seeder (DM-9): registering a household
/// seeds exactly Breakfast, Lunch, and Dinner slots in ordinal order, scoped to that household.
/// Mirrors <c>RecipesReferenceDataTests</c>.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class MealSlotSeedTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private HouseholdId _otherHousehold;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
        _otherHousehold = HouseholdId.New();

        // Seed only _household — _otherHousehold is left untouched to prove household scoping.
        await using var seedDb = NewMealPlanningDb(_household);
        var seeder = new MealPlanningReferenceDataSeeder(seedDb, SystemClock.Instance);
        await seeder.SeedAsync(_household);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "Seeding a household creates exactly 3 default slots: Breakfast, Lunch, Dinner in ordinal order")]
    public async Task Seeding_Creates_Three_Default_Slots()
    {
        await using var read = NewMealPlanningDb(_household);
        var slots = await read.MealSlots.OrderBy(s => s.Ordinal).ToListAsync();

        Assert.Equal(3, slots.Count);
        Assert.All(slots, s => Assert.Equal(_household, s.HouseholdId));
        Assert.All(slots, s => Assert.Null(s.ArchivedAt));

        Assert.Equal("Breakfast", slots[0].Label);
        Assert.Equal(1, slots[0].Ordinal);

        Assert.Equal("Lunch", slots[1].Label);
        Assert.Equal(2, slots[1].Ordinal);

        Assert.Equal("Dinner", slots[2].Label);
        Assert.Equal(3, slots[2].Ordinal);
    }

    [Fact(DisplayName = "Seeded slots are household-scoped: an unseeded household sees zero slots")]
    public async Task Slots_Are_Household_Scoped()
    {
        await using var read = NewMealPlanningDb(_otherHousehold);
        var slots = await read.MealSlots.ToListAsync();

        Assert.Empty(slots);
    }

    [Fact(DisplayName = "Seeding a household creates exactly one MealSlotConfig for that household")]
    public async Task Seeding_Creates_One_Config()
    {
        await using var read = NewMealPlanningDb(_household);
        var configs = await read.MealSlotConfigs.ToListAsync();

        var config = Assert.Single(configs);
        Assert.Equal(_household, config.HouseholdId);
    }

    private DbContextOptions<MealPlanningDbContext> MealPlanningOptions() =>
        new DbContextOptionsBuilder<MealPlanningDbContext>().UseNpgsql(db.ConnectionString).Options;

    private MealPlanningDbContext NewMealPlanningDb(HouseholdId household)
    {
        var ctx = new MealPlanningDbContext(MealPlanningOptions());
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }
}
