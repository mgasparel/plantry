using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Xunit;

namespace Plantry.Tests.Unit.MealPlanning.Domain;

/// <summary>
/// L1 unit tests for <see cref="MealSlotConfig"/> invariants (M9/M10).
/// </summary>
public sealed class MealSlotConfigTests
{
    private static readonly HouseholdId HouseholdId = HouseholdId.New();
    private static readonly IClock Clock = SystemClock.Instance;

    private static MealSlotConfig CreateDefault() =>
        MealSlotConfig.CreateWithDefaults(HouseholdId, Clock);

    // ── AddSlot ──────────────────────────────────────────────────────────────

    [Fact]
    public void AddSlot_Appends_At_Next_Ordinal()
    {
        var config = CreateDefault(); // has 3 active slots (Breakfast=1, Lunch=2, Dinner=3)

        var slot = config.AddSlot("Supper", Clock);

        Assert.Equal(4, slot.Ordinal);
        Assert.Equal("Supper", slot.Label);
        Assert.True(slot.IsActive);
    }

    [Fact]
    public void AddSlot_Rejects_Blank_Label()
    {
        var config = CreateDefault();

        Assert.Throws<InvalidOperationException>(() => config.AddSlot("  ", Clock));
    }

    [Fact]
    public void AddSlot_Rejects_Duplicate_Label_Case_Insensitive()
    {
        var config = CreateDefault();

        Assert.Throws<InvalidOperationException>(() => config.AddSlot("BREAKFAST", Clock));
    }

    [Fact]
    public void AddSlot_Trims_Label()
    {
        var config = CreateDefault();

        var slot = config.AddSlot("  Midnight Snack  ", Clock);

        Assert.Equal("Midnight Snack", slot.Label);
    }

    // ── RenameSlot ──────────────────────────────────────────────────────────

    [Fact]
    public void RenameSlot_Updates_Label()
    {
        var config = CreateDefault();
        var slotId = config.Slots.First(s => s.Label == "Lunch").Id;

        config.RenameSlot(slotId, "Midday meal", Clock);

        Assert.Equal("Midday meal", config.Slots.First(s => s.Id == slotId).Label);
    }

    [Fact]
    public void RenameSlot_Rejects_Blank_Label()
    {
        var config = CreateDefault();
        var slotId = config.Slots.First(s => s.Label == "Lunch").Id;

        Assert.Throws<InvalidOperationException>(() => config.RenameSlot(slotId, "", Clock));
    }

    [Fact]
    public void RenameSlot_Rejects_Duplicate_Label_Among_Active()
    {
        var config = CreateDefault();
        var lunchId = config.Slots.First(s => s.Label == "Lunch").Id;

        Assert.Throws<InvalidOperationException>(() => config.RenameSlot(lunchId, "Breakfast", Clock));
    }

    [Fact]
    public void RenameSlot_Allows_Same_Name_On_Self()
    {
        var config = CreateDefault();
        var lunchId = config.Slots.First(s => s.Label == "Lunch").Id;

        // Should not throw — renaming to the same label is a no-op but not an error
        config.RenameSlot(lunchId, "Lunch", Clock);
        Assert.Equal("Lunch", config.Slots.First(s => s.Id == lunchId).Label);
    }

    [Fact]
    public void RenameSlot_Throws_For_Unknown_Id()
    {
        var config = CreateDefault();

        Assert.Throws<InvalidOperationException>(() =>
            config.RenameSlot(MealSlotId.New(), "Something", Clock));
    }

    // ── ReorderSlots ─────────────────────────────────────────────────────────

    [Fact]
    public void ReorderSlots_Renumbers_Ordinals_Contiguously()
    {
        var config = CreateDefault();
        var breakfast = config.Slots.First(s => s.Label == "Breakfast").Id;
        var lunch = config.Slots.First(s => s.Label == "Lunch").Id;
        var dinner = config.Slots.First(s => s.Label == "Dinner").Id;

        // Reverse order: Dinner → Lunch → Breakfast
        config.ReorderSlots([dinner, lunch, breakfast], Clock);

        var slots = config.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).ToList();
        Assert.Equal("Dinner", slots[0].Label);
        Assert.Equal(1, slots[0].Ordinal);
        Assert.Equal("Lunch", slots[1].Label);
        Assert.Equal(2, slots[1].Ordinal);
        Assert.Equal("Breakfast", slots[2].Label);
        Assert.Equal(3, slots[2].Ordinal);
    }

    [Fact]
    public void ReorderSlots_Rejects_Wrong_Count()
    {
        var config = CreateDefault();
        var breakfast = config.Slots.First(s => s.Label == "Breakfast").Id;

        // Missing Lunch and Dinner
        Assert.Throws<InvalidOperationException>(() => config.ReorderSlots([breakfast], Clock));
    }

    [Fact]
    public void ReorderSlots_Rejects_Unknown_Id()
    {
        var config = CreateDefault();
        var breakfast = config.Slots.First(s => s.Label == "Breakfast").Id;
        var lunch = config.Slots.First(s => s.Label == "Lunch").Id;

        // Replace Dinner with a random unknown ID
        Assert.Throws<InvalidOperationException>(() =>
            config.ReorderSlots([breakfast, lunch, MealSlotId.New()], Clock));
    }

    // ── SetDefaultAttendees ───────────────────────────────────────────────────

    [Fact]
    public void SetDefaultAttendees_Stores_MemberIds()
    {
        var config = CreateDefault();
        var slotId = config.Slots.First(s => s.Label == "Breakfast").Id;
        var memberA = Guid.NewGuid();
        var memberB = Guid.NewGuid();

        config.SetDefaultAttendees(slotId, [memberA, memberB], Clock);

        var slot = config.Slots.First(s => s.Id == slotId);
        Assert.Contains(memberA, slot.DefaultAttendees);
        Assert.Contains(memberB, slot.DefaultAttendees);
    }

    [Fact]
    public void SetDefaultAttendees_Replaces_Existing_List()
    {
        var config = CreateDefault();
        var slotId = config.Slots.First(s => s.Label == "Breakfast").Id;
        var memberA = Guid.NewGuid();
        var memberB = Guid.NewGuid();

        config.SetDefaultAttendees(slotId, [memberA], Clock);
        config.SetDefaultAttendees(slotId, [memberB], Clock);

        var slot = config.Slots.First(s => s.Id == slotId);
        Assert.DoesNotContain(memberA, slot.DefaultAttendees);
        Assert.Contains(memberB, slot.DefaultAttendees);
    }

    // ── ArchiveSlot ──────────────────────────────────────────────────────────

    [Fact]
    public void ArchiveSlot_Sets_ArchivedAt_And_Slot_Becomes_Inactive()
    {
        var config = CreateDefault();
        var slotId = config.Slots.First(s => s.Label == "Lunch").Id;

        config.ArchiveSlot(slotId, Clock);

        var slot = config.Slots.First(s => s.Id == slotId);
        Assert.NotNull(slot.ArchivedAt);
        Assert.False(slot.IsActive);
    }

    [Fact]
    public void ArchiveSlot_Renumbers_Remaining_Active_Ordinals_Contiguously()
    {
        var config = CreateDefault(); // Breakfast=1, Lunch=2, Dinner=3
        var lunchId = config.Slots.First(s => s.Label == "Lunch").Id;

        config.ArchiveSlot(lunchId, Clock);

        var active = config.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).ToList();
        Assert.Equal(2, active.Count);
        Assert.Equal(1, active[0].Ordinal); // Breakfast
        Assert.Equal(2, active[1].Ordinal); // Dinner (moved from 3 → 2)
    }

    [Fact]
    public void ArchiveSlot_Preserves_Archived_Slot_Row()
    {
        var config = CreateDefault();
        var lunchId = config.Slots.First(s => s.Label == "Lunch").Id;

        config.ArchiveSlot(lunchId, Clock);

        // All three slots still present — archived not deleted
        Assert.Equal(3, config.Slots.Count);
    }

    [Fact]
    public void ArchiveSlot_Throws_For_Already_Archived()
    {
        var config = CreateDefault();
        var lunchId = config.Slots.First(s => s.Label == "Lunch").Id;

        config.ArchiveSlot(lunchId, Clock);

        // Second archive attempt should throw
        Assert.Throws<InvalidOperationException>(() => config.ArchiveSlot(lunchId, Clock));
    }

    [Fact]
    public void AddSlot_After_Archive_Assigns_Correct_Next_Ordinal()
    {
        var config = CreateDefault(); // active: B=1, L=2, D=3
        var lunchId = config.Slots.First(s => s.Label == "Lunch").Id;
        config.ArchiveSlot(lunchId, Clock); // active: B=1, D=2

        var newSlot = config.AddSlot("Brunch", Clock); // active: B=1, D=2, Brunch=3

        Assert.Equal(3, newSlot.Ordinal);
    }

    [Fact]
    public void Ordinals_Remain_Contiguous_After_All_Mutations()
    {
        var config = CreateDefault();
        config.AddSlot("Snack", Clock);       // B=1 L=2 D=3 Snack=4
        var lunchId = config.Slots.First(s => s.Label == "Lunch").Id;
        config.ArchiveSlot(lunchId, Clock);   // active: B=1 D=2 Snack=3

        var ordinals = config.Slots.Where(s => s.IsActive).Select(s => s.Ordinal).OrderBy(o => o).ToList();
        Assert.Equal([1, 2, 3], ordinals);
    }
}
