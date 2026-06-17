using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Xunit;

namespace Plantry.Tests.Unit.MealPlanning.Application;

/// <summary>
/// L2 unit tests for <see cref="ManageSlotsService"/> using fake doubles.
/// </summary>
public sealed class ManageSlotsServiceTests
{
    private static readonly HouseholdId HouseholdId = HouseholdId.New();
    private static readonly IClock Clock = SystemClock.Instance;

    private static ManageSlotsService BuildService(
        FakeMealSlotConfigRepository repo,
        FakeHouseholdMemberReader? reader = null)
        => new(repo, reader ?? new FakeHouseholdMemberReader(), Clock);

    // ── GetSlotsAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSlotsAsync_Returns_Null_When_No_Config()
    {
        var repo = new FakeMealSlotConfigRepository(null);
        var svc = BuildService(repo);

        var result = await svc.GetSlotsAsync(HouseholdId);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetSlotsAsync_Returns_Config_When_Present()
    {
        var config = MealSlotConfig.CreateWithDefaults(HouseholdId, Clock);
        var repo = new FakeMealSlotConfigRepository(config);
        var svc = BuildService(repo);

        var result = await svc.GetSlotsAsync(HouseholdId);

        Assert.NotNull(result);
        Assert.Equal(3, result!.Slots.Count(s => s.IsActive));
    }

    // ── AddSlotAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AddSlotAsync_Adds_Slot_To_Existing_Config_And_Saves()
    {
        var config = MealSlotConfig.CreateWithDefaults(HouseholdId, Clock);
        var repo = new FakeMealSlotConfigRepository(config);
        var svc = BuildService(repo);

        await svc.AddSlotAsync(HouseholdId, "Supper");

        Assert.Equal(4, config.Slots.Count(s => s.IsActive));
        Assert.True(repo.SavedCount > 0);
    }

    [Fact]
    public async Task AddSlotAsync_Creates_Config_When_None_Exists_And_Saves()
    {
        var repo = new FakeMealSlotConfigRepository(null);
        var svc = BuildService(repo);

        await svc.AddSlotAsync(HouseholdId, "Extra");

        // config was created with defaults (3) + 1 extra
        Assert.NotNull(repo.Stored);
        Assert.Equal(4, repo.Stored!.Slots.Count(s => s.IsActive));
        Assert.True(repo.SavedCount > 0);
    }

    // ── RenameSlotAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task RenameSlotAsync_Renames_Slot_And_Saves()
    {
        var config = MealSlotConfig.CreateWithDefaults(HouseholdId, Clock);
        var lunchId = config.Slots.First(s => s.Label == "Lunch").Id;
        var repo = new FakeMealSlotConfigRepository(config);
        var svc = BuildService(repo);

        await svc.RenameSlotAsync(HouseholdId, lunchId, "Midday meal");

        Assert.Equal("Midday meal", config.Slots.First(s => s.Id == lunchId).Label);
        Assert.True(repo.SavedCount > 0);
    }

    [Fact]
    public async Task RenameSlotAsync_Throws_When_Config_Missing()
    {
        var repo = new FakeMealSlotConfigRepository(null);
        var svc = BuildService(repo);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RenameSlotAsync(HouseholdId, MealSlotId.New(), "X"));
    }

    // ── ArchiveSlotAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task ArchiveSlotAsync_Archives_Slot_And_Saves()
    {
        var config = MealSlotConfig.CreateWithDefaults(HouseholdId, Clock);
        var lunchId = config.Slots.First(s => s.Label == "Lunch").Id;
        var repo = new FakeMealSlotConfigRepository(config);
        var svc = BuildService(repo);

        await svc.ArchiveSlotAsync(HouseholdId, lunchId);

        var slot = config.Slots.First(s => s.Id == lunchId);
        Assert.False(slot.IsActive);
        Assert.True(repo.SavedCount > 0);
    }

    // ── ReorderSlotsAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task ReorderSlotsAsync_Reorders_And_Saves()
    {
        var config = MealSlotConfig.CreateWithDefaults(HouseholdId, Clock);
        var breakfastId = config.Slots.First(s => s.Label == "Breakfast").Id;
        var lunchId = config.Slots.First(s => s.Label == "Lunch").Id;
        var dinnerId = config.Slots.First(s => s.Label == "Dinner").Id;
        var repo = new FakeMealSlotConfigRepository(config);
        var svc = BuildService(repo);

        await svc.ReorderSlotsAsync(HouseholdId, [dinnerId, lunchId, breakfastId]);

        var active = config.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).ToList();
        Assert.Equal("Dinner", active[0].Label);
        Assert.True(repo.SavedCount > 0);
    }

    // ── SetDefaultAttendeesAsync ─────────────────────────────────────────────

    [Fact]
    public async Task SetDefaultAttendeesAsync_Stores_Members_And_Saves()
    {
        var config = MealSlotConfig.CreateWithDefaults(HouseholdId, Clock);
        var slotId = config.Slots.First(s => s.Label == "Dinner").Id;
        var member = Guid.NewGuid();
        var repo = new FakeMealSlotConfigRepository(config);
        var svc = BuildService(repo);

        await svc.SetDefaultAttendeesAsync(HouseholdId, slotId, [member]);

        Assert.Contains(member, config.Slots.First(s => s.Id == slotId).DefaultAttendees);
        Assert.True(repo.SavedCount > 0);
    }

    // ── GetMembersAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetMembersAsync_Delegates_To_Reader()
    {
        var repo = new FakeMealSlotConfigRepository(null);
        var member = new HouseholdMember(Guid.NewGuid(), "Alice");
        var reader = new FakeHouseholdMemberReader(member);
        var svc = BuildService(repo, reader);

        var result = await svc.GetMembersAsync(HouseholdId.Value);

        Assert.Single(result);
        Assert.Equal("Alice", result[0].DisplayName);
    }
}

// ── test doubles ─────────────────────────────────────────────────────────────

public sealed class FakeMealSlotConfigRepository(MealSlotConfig? initial) : IMealSlotConfigRepository
{
    public MealSlotConfig? Stored { get; private set; } = initial;
    public int SavedCount { get; private set; }

    public Task<MealSlotConfig?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        => Task.FromResult(Stored);

    public Task AddAsync(MealSlotConfig config, CancellationToken ct = default)
    {
        Stored = config;
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SavedCount++;
        return Task.CompletedTask;
    }
}

public sealed class FakeHouseholdMemberReader(params HouseholdMember[] members) : IHouseholdMemberReader
{
    public Task<IReadOnlyList<HouseholdMember>> GetMembersAsync(
        Guid householdId,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<HouseholdMember>>(members);
}
