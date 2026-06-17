using Microsoft.EntityFrameworkCore;
using Plantry.MealPlanning.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.MealPlanning;

/// <summary>
/// L3 integration tests for MealSlotConfig mutations persisting through the real Postgres schema
/// (slot add, rename, reorder, set-attendees, archive). Verifies round-trip correctness.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class MealSlotConfigPersistenceTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        // Seed the config with defaults
        await using var seedDb = NewMealPlanningDb(_household);
        var seeder = new MealPlanningReferenceDataSeeder(seedDb, SystemClock.Instance);
        await seeder.SeedAsync(_household);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "AddSlot: new slot persists and is visible after reload")]
    public async Task AddSlot_Persists()
    {
        // mutate
        await using (var writeDb = NewMealPlanningDb(_household))
        {
            var config = await writeDb.MealSlotConfigs.Include(c => c.Slots).SingleAsync();
            config.AddSlot("Supper", SystemClock.Instance);
            await writeDb.SaveChangesAsync();
        }

        // verify
        await using var readDb = NewMealPlanningDb(_household);
        var slots = await readDb.MealSlots.Where(s => s.ArchivedAt == null).OrderBy(s => s.Ordinal).ToListAsync();
        Assert.Equal(4, slots.Count);
        Assert.Contains(slots, s => s.Label == "Supper");
        Assert.Equal(4, slots.Single(s => s.Label == "Supper").Ordinal);
    }

    [Fact(DisplayName = "RenameSlot: updated label persists after reload")]
    public async Task RenameSlot_Persists()
    {
        Guid slotId;

        await using (var writeDb = NewMealPlanningDb(_household))
        {
            var config = await writeDb.MealSlotConfigs.Include(c => c.Slots).SingleAsync();
            var slot = config.Slots.Single(s => s.Label == "Lunch");
            slotId = slot.Id.Value;
            config.RenameSlot(slot.Id, "Midday meal", SystemClock.Instance);
            await writeDb.SaveChangesAsync();
        }

        await using var readDb = NewMealPlanningDb(_household);
        var reloaded = await readDb.MealSlots.SingleAsync(s => s.Id == Plantry.MealPlanning.Domain.MealSlotId.From(slotId));
        Assert.Equal("Midday meal", reloaded.Label);
    }

    [Fact(DisplayName = "ReorderSlots: ordinals persisted correctly after reload")]
    public async Task ReorderSlots_Persists()
    {
        Guid breakfastId, lunchId, dinnerId;

        await using (var writeDb = NewMealPlanningDb(_household))
        {
            var config = await writeDb.MealSlotConfigs.Include(c => c.Slots).SingleAsync();
            breakfastId = config.Slots.Single(s => s.Label == "Breakfast").Id.Value;
            lunchId = config.Slots.Single(s => s.Label == "Lunch").Id.Value;
            dinnerId = config.Slots.Single(s => s.Label == "Dinner").Id.Value;

            config.ReorderSlots([
                Plantry.MealPlanning.Domain.MealSlotId.From(dinnerId),
                Plantry.MealPlanning.Domain.MealSlotId.From(lunchId),
                Plantry.MealPlanning.Domain.MealSlotId.From(breakfastId),
            ], SystemClock.Instance);
            await writeDb.SaveChangesAsync();
        }

        await using var readDb = NewMealPlanningDb(_household);
        var slots = await readDb.MealSlots.Where(s => s.ArchivedAt == null).OrderBy(s => s.Ordinal).ToListAsync();
        Assert.Equal("Dinner", slots[0].Label);
        Assert.Equal("Lunch", slots[1].Label);
        Assert.Equal("Breakfast", slots[2].Label);
        Assert.Equal(1, slots[0].Ordinal);
        Assert.Equal(2, slots[1].Ordinal);
        Assert.Equal(3, slots[2].Ordinal);
    }

    [Fact(DisplayName = "SetDefaultAttendees: attendee GUIDs persisted after reload")]
    public async Task SetDefaultAttendees_Persists()
    {
        var memberA = Guid.NewGuid();
        var memberB = Guid.NewGuid();
        Guid slotId;

        await using (var writeDb = NewMealPlanningDb(_household))
        {
            var config = await writeDb.MealSlotConfigs.Include(c => c.Slots).SingleAsync();
            var slot = config.Slots.Single(s => s.Label == "Dinner");
            slotId = slot.Id.Value;
            config.SetDefaultAttendees(slot.Id, [memberA, memberB], SystemClock.Instance);
            await writeDb.SaveChangesAsync();
        }

        await using var readDb = NewMealPlanningDb(_household);
        var reloaded = await readDb.MealSlots.SingleAsync(
            s => s.Id == Plantry.MealPlanning.Domain.MealSlotId.From(slotId));
        Assert.Contains(memberA, reloaded.DefaultAttendees);
        Assert.Contains(memberB, reloaded.DefaultAttendees);
    }

    [Fact(DisplayName = "ArchiveSlot: ArchivedAt set and ordinals renumbered after reload")]
    public async Task ArchiveSlot_Persists()
    {
        Guid lunchId;

        await using (var writeDb = NewMealPlanningDb(_household))
        {
            var config = await writeDb.MealSlotConfigs.Include(c => c.Slots).SingleAsync();
            var slot = config.Slots.Single(s => s.Label == "Lunch");
            lunchId = slot.Id.Value;
            config.ArchiveSlot(slot.Id, SystemClock.Instance);
            await writeDb.SaveChangesAsync();
        }

        await using var readDb = NewMealPlanningDb(_household);

        // Archived slot still exists (soft-delete)
        var archived = await readDb.MealSlots.SingleAsync(
            s => s.Id == Plantry.MealPlanning.Domain.MealSlotId.From(lunchId));
        Assert.NotNull(archived.ArchivedAt);

        // Active ordinals are contiguous 1..2
        var active = await readDb.MealSlots.Where(s => s.ArchivedAt == null)
            .OrderBy(s => s.Ordinal).ToListAsync();
        Assert.Equal(2, active.Count);
        Assert.Equal(1, active[0].Ordinal);
        Assert.Equal(2, active[1].Ordinal);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private DbContextOptions<MealPlanningDbContext> MealPlanningOptions() =>
        new DbContextOptionsBuilder<MealPlanningDbContext>().UseNpgsql(db.ConnectionString).Options;

    private MealPlanningDbContext NewMealPlanningDb(HouseholdId household)
    {
        var ctx = new MealPlanningDbContext(MealPlanningOptions());
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }
}
