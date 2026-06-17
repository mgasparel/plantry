using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Xunit;

namespace Plantry.Tests.Unit.MealPlanning.Application;

/// <summary>
/// L2 unit tests for <see cref="SetPreferences"/> (acceptance criterion L2).
/// Covers: category grouping resolves; lazy create on first edit; no-op when setting Neutral with no existing profile.
/// </summary>
public sealed class SetPreferencesTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly IClock Clock = SystemClock.Instance;

    private readonly FakeUserPreferenceRepository _prefsRepo = new();
    private readonly FakeTagReader _tagReader = new();
    private readonly FakeHouseholdMemberReader _memberReader = new();
    private readonly FakeTenantContext _tenant = new(Household.Value);

    private SetPreferences Service => new(_prefsRepo, _tagReader, _memberReader, _tenant, Clock);

    [Fact(DisplayName = "GetViewModel returns tags grouped by cosmetic category")]
    public async Task GetViewModel_GroupsTagsByCategory()
    {
        _tagReader.AddGroup("Diet", 150, [
            new TagSummary(Guid.NewGuid(), "Vegetarian", "Diet", 150),
            new TagSummary(Guid.NewGuid(), "Vegan", "Diet", 150),
        ]);
        _tagReader.AddGroup("Protein", 28, [
            new TagSummary(Guid.NewGuid(), "Eggs", "Protein", 28),
        ]);

        var vm = await Service.GetViewModelAsync(UserId);

        Assert.Equal(2, vm.Groups.Count);
        Assert.Equal("Diet", vm.Groups[0].Category);
        Assert.Equal(2, vm.Groups[0].Tags.Count);
        Assert.Equal("Protein", vm.Groups[1].Category);
        Assert.Equal(1, vm.Groups[1].Tags.Count);
    }

    [Fact(DisplayName = "GetViewModel: tags with no existing stances default to Neutral")]
    public async Task GetViewModel_DefaultsToNeutral_WhenNoPrefsRow()
    {
        var tagId = Guid.NewGuid();
        _tagReader.AddGroup("Flavor", 330, [new TagSummary(tagId, "Spicy", "Flavor", 330)]);

        var vm = await Service.GetViewModelAsync(UserId);

        var tag = Assert.Single(vm.Groups[0].Tags);
        Assert.Equal("Neutral", tag.Stance);
        Assert.Equal(0, vm.SetCount);
    }

    [Fact(DisplayName = "GetViewModel: existing stances appear in the view model")]
    public async Task GetViewModel_ShowsExistingStance()
    {
        var tagId = Guid.NewGuid();
        _tagReader.AddGroup("Protein", 28, [new TagSummary(tagId, "Shellfish", "Protein", 28)]);

        // Seed a preference row with Restricted stance on this tag.
        var pref = UserPreference.Create(Household, UserId, Clock);
        pref.SetStance(tagId, "Restricted", Clock);
        _prefsRepo.Seed(pref);

        var vm = await Service.GetViewModelAsync(UserId);

        var tag = Assert.Single(vm.Groups[0].Tags);
        Assert.Equal("Restricted", tag.Stance);
        Assert.Equal(1, vm.SetCount);
    }

    [Fact(DisplayName = "SetStanceAsync creates aggregate lazily on first edit (M6)")]
    public async Task SetStanceAsync_CreatesAggregateLazily()
    {
        var tagId = Guid.NewGuid();

        await Service.SetStanceAsync(UserId, tagId, "Required");

        var saved = Assert.Single(_prefsRepo.Saved);
        Assert.Equal(UserId, saved.UserId);
        var stance = Assert.Single(saved.TagStances);
        Assert.Equal(tagId, stance.TagId);
        Assert.Equal("Required", stance.Stance);
    }

    [Fact(DisplayName = "SetStanceAsync Neutral on no existing profile is a no-op (does not create aggregate)")]
    public async Task SetStanceAsync_Neutral_NoProfile_NoOp()
    {
        await Service.SetStanceAsync(UserId, Guid.NewGuid(), "Neutral");

        Assert.Empty(_prefsRepo.Saved);
    }

    [Fact(DisplayName = "SetStanceAsync updates existing profile without duplicating")]
    public async Task SetStanceAsync_UpdatesExistingProfile()
    {
        var tagId = Guid.NewGuid();
        var pref = UserPreference.Create(Household, UserId, Clock);
        pref.SetStance(tagId, "Preferred", Clock);
        _prefsRepo.Seed(pref);

        await Service.SetStanceAsync(UserId, tagId, "Disliked");

        var saved = _prefsRepo.FindByUserId(UserId)!;
        var stance = Assert.Single(saved.TagStances);
        Assert.Equal("Disliked", stance.Stance);
    }

    [Fact(DisplayName = "ResetToNeutralAsync clears all stances")]
    public async Task ResetToNeutralAsync_ClearsAllStances()
    {
        var pref = UserPreference.Create(Household, UserId, Clock);
        pref.SetStance(Guid.NewGuid(), "Required", Clock);
        pref.SetStance(Guid.NewGuid(), "Restricted", Clock);
        _prefsRepo.Seed(pref);

        await Service.ResetToNeutralAsync(UserId);

        Assert.Empty(_prefsRepo.FindByUserId(UserId)!.TagStances);
    }
}

// ── Fakes ──────────────────────────────────────────────────────────────────────

internal sealed class FakeUserPreferenceRepository : IUserPreferenceRepository
{
    private readonly List<UserPreference> _store = [];
    public IReadOnlyList<UserPreference> Saved => _store;

    public void Seed(UserPreference pref) => _store.Add(pref);

    public UserPreference? FindByUserId(Guid userId) => _store.FirstOrDefault(p => p.UserId == userId);

    public Task<UserPreference?> FindByUserIdAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(_store.FirstOrDefault(p => p.UserId == userId));

    public Task AddAsync(UserPreference preference, CancellationToken ct = default)
    {
        _store.Add(preference);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakeTagReader : ITagReader
{
    private readonly List<TagGroup> _groups = [];

    public void AddGroup(string category, int? hue, IReadOnlyList<TagSummary> tags) =>
        _groups.Add(new TagGroup(category, hue, tags));

    public Task<IReadOnlyList<TagGroup>> ListGroupedAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TagGroup>>(_groups);
}

internal sealed class FakeHouseholdMemberReader : IHouseholdMemberReader
{
    public Task<IReadOnlyList<HouseholdMember>> ListMembersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HouseholdMember>>([
            new HouseholdMember(Guid.NewGuid(), "Demo User", "DU"),
        ]);
}

internal sealed class FakeTenantContext(Guid? householdId) : ITenantContext
{
    public Guid? HouseholdId { get; } = householdId;
}
