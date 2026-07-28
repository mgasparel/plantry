using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Identity.Application;
using Plantry.Identity.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Unit.Identity.Application;

/// <summary>
/// L1 tests for <see cref="HouseholdExpiryDefaultsService"/> (the <see cref="IHouseholdExpiryDefaults"/>
/// read source that feeds Catalog's <c>ExpiryDefaultResolver</c> freeze/thaw fallback, plus the
/// /Settings/Expiry write path, plantry-qckx) and the <see cref="Household.DefaultDueDaysAfterFreezing"/>/
/// <see cref="Household.DefaultDueDaysAfterThawing"/> settings (plantry-hh1f). Defaults are 90/3; the
/// write path requires a household in context and rejects a day count outside
/// [<see cref="HouseholdExpiryDefaultsService.MinDays"/>, <see cref="HouseholdExpiryDefaultsService.MaxDays"/>]
/// = [0, 3650] with <see cref="Error.Custom(string, string)"/> (plantry-qckx tightened this from letting
/// a negative value fall through to the aggregate's <see cref="ArgumentOutOfRangeException"/> — the
/// service now validates before ever reaching the aggregate, mirroring
/// <c>ExpiringSoonSettingsService.SetDaysAsync</c>). Mirrors <c>DisplayCurrencyServiceTests</c>'s shape.
///
/// This service does not (and never did) own a per-household "expiry warning days" setting — the
/// domain column that briefly existed under that name, along with the Get/SetWarningDaysAsync
/// wrapper methods that briefly lived here, were retired in the same plantry-qckx change as dead
/// configuration duplicating the Inventory context's live "expiring soon" horizon (see
/// <c>ExpiringSoonSettingsServiceTests</c> for that coverage).
/// </summary>
public sealed class HouseholdExpiryDefaultsServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid _household = Guid.Parse("cccccccc-0003-0000-0000-000000000001");

    private static HouseholdExpiryDefaultsService Service(FakeHouseholdRepository repo, Guid? household) =>
        new(repo, new FakeTenantContext(household), NullLogger<HouseholdExpiryDefaultsService>.Instance);

    private Household SeededHousehold(int? afterFreezing = null, int? afterThawing = null)
    {
        var household = Household.Create("Test", new FixedClock(Now));
        if (afterFreezing is { } f) household.SetDefaultDueDaysAfterFreezing(f);
        if (afterThawing is { } t) household.SetDefaultDueDaysAfterThawing(t);
        return household;
    }

    // ── Read (GetAsync) ───────────────────────────────────────────────────────

    [Fact(DisplayName = "GetAsync defaults to 90/3 when there is no household in context")]
    public async Task Get_Defaults_NinetyThree_When_No_Household()
    {
        var defaults = await Service(new FakeHouseholdRepository(), household: null).GetAsync();

        Assert.Equal((90, 3), defaults);
        Assert.Equal(90, HouseholdExpiryDefaultsService.DefaultAfterFreezing);
        Assert.Equal(3, HouseholdExpiryDefaultsService.DefaultAfterThawing);
    }

    [Fact(DisplayName = "GetAsync defaults to 90/3 when the household row is not found")]
    public async Task Get_Defaults_NinetyThree_When_Row_Missing()
    {
        var defaults = await Service(new FakeHouseholdRepository(), _household).GetAsync();
        Assert.Equal((90, 3), defaults);
    }

    [Fact(DisplayName = "GetAsync reflects the household's persisted defaults")]
    public async Task Get_Reflects_Household_Defaults()
    {
        var repo = new FakeHouseholdRepository(HouseholdId.From(_household), SeededHousehold(45, 5));
        Assert.Equal((45, 5), await Service(repo, _household).GetAsync());
    }

    // ── Write (SetAllAsync, plantry-hw39) ─────────────────────────────────────

    [Fact(DisplayName = "SetAllAsync persists both fields in one load/mutate/save and returns the persisted tuple")]
    public async Task SetAll_Persists_Both_In_One_SaveChanges_Call()
    {
        var repo = new FakeHouseholdRepository(HouseholdId.From(_household), SeededHousehold());
        var service = Service(repo, _household);

        var result = await service.SetAllAsync(120, 7);

        Assert.True(result.IsSuccess);
        Assert.Equal((120, 7), result.Value);
        Assert.Equal(1, repo.SaveChangesCalls);
        Assert.Equal((120, 7), await service.GetAsync());
    }

    [Fact(DisplayName = "SetAllAsync returns Unauthorized when there is no household in context, and writes nothing")]
    public async Task SetAll_Requires_Household()
    {
        var repo = new FakeHouseholdRepository();
        var result = await Service(repo, household: null).SetAllAsync(120, 7);

        Assert.True(result.IsFailure);
        Assert.Equal(Error.Unauthorized, result.Error);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    [Fact(DisplayName = "SetAllAsync returns NotFound when the household row is missing, and writes nothing")]
    public async Task SetAll_NotFound_When_Row_Missing()
    {
        var repo = new FakeHouseholdRepository();
        var result = await Service(repo, _household).SetAllAsync(120, 7);

        Assert.True(result.IsFailure);
        Assert.Equal(Error.NotFound, result.Error);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    [Fact(DisplayName = "SetAllAsync rejects when EITHER value is out of range, validated before the household is loaded — writes nothing")]
    public async Task SetAll_Rejects_When_Either_Value_OutOfRange()
    {
        var repo = new FakeHouseholdRepository(HouseholdId.From(_household), SeededHousehold());
        var service = Service(repo, _household);

        var freezingOutOfRange = await service.SetAllAsync(HouseholdExpiryDefaultsService.MaxDays + 1, 7);
        Assert.True(freezingOutOfRange.IsFailure);
        Assert.Equal("Identity.InvalidExpiryDefaultDays", freezingOutOfRange.Error.Code);

        var thawingOutOfRange = await service.SetAllAsync(120, -1);
        Assert.True(thawingOutOfRange.IsFailure);
        Assert.Equal("Identity.InvalidExpiryDefaultDays", thawingOutOfRange.Error.Code);

        var freezingBelowMin = await service.SetAllAsync(-1, 7);
        Assert.True(freezingBelowMin.IsFailure);
        Assert.Equal("Identity.InvalidExpiryDefaultDays", freezingBelowMin.Error.Code);

        var thawingAboveMax = await service.SetAllAsync(120, HouseholdExpiryDefaultsService.MaxDays + 1);
        Assert.True(thawingAboveMax.IsFailure);
        Assert.Equal("Identity.InvalidExpiryDefaultDays", thawingAboveMax.Error.Code);

        Assert.Equal(0, repo.SaveChangesCalls);
        // Neither field changed — a rejected combined write must not partially apply.
        Assert.Equal((90, 3), await service.GetAsync());
    }

    [Fact(DisplayName = "SetAllAsync accepts the boundary values Min (0) and Max (3650) for both fields")]
    public async Task SetAll_Accepts_Boundaries()
    {
        var repo = new FakeHouseholdRepository(HouseholdId.From(_household), SeededHousehold());
        var service = Service(repo, _household);

        var result = await service.SetAllAsync(
            HouseholdExpiryDefaultsService.MinDays, HouseholdExpiryDefaultsService.MaxDays);

        Assert.True(result.IsSuccess);
        Assert.Equal((HouseholdExpiryDefaultsService.MinDays, HouseholdExpiryDefaultsService.MaxDays), result.Value);
        Assert.Equal(1, repo.SaveChangesCalls);

        var mirrored = await service.SetAllAsync(
            HouseholdExpiryDefaultsService.MaxDays, HouseholdExpiryDefaultsService.MinDays);

        Assert.True(mirrored.IsSuccess);
        Assert.Equal((HouseholdExpiryDefaultsService.MaxDays, HouseholdExpiryDefaultsService.MinDays), mirrored.Value);
        Assert.Equal(2, repo.SaveChangesCalls);
    }

    // ── Aggregate invariant ───────────────────────────────────────────────────

    [Fact(DisplayName = "A freshly created household defaults to 90 days after freezing and 3 after thawing")]
    public void Create_Defaults_NinetyAndThree()
    {
        var household = Household.Create("Test", new FixedClock(Now));
        Assert.Equal(90, household.DefaultDueDaysAfterFreezing);
        Assert.Equal(3, household.DefaultDueDaysAfterThawing);
    }

    [Fact(DisplayName = "SetDefaultDueDaysAfterFreezing rejects a negative value")]
    public void SetDefaultDueDaysAfterFreezing_Rejects_Negative()
    {
        var household = Household.Create("Test", new FixedClock(Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => household.SetDefaultDueDaysAfterFreezing(-1));
    }

    [Fact(DisplayName = "SetDefaultDueDaysAfterThawing rejects a negative value")]
    public void SetDefaultDueDaysAfterThawing_Rejects_Negative()
    {
        var household = Household.Create("Test", new FixedClock(Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => household.SetDefaultDueDaysAfterThawing(-1));
    }

    [Fact(DisplayName = "SetDefaultDueDaysAfterFreezing accepts zero (never re-extends, but is a valid explicit setting)")]
    public void SetDefaultDueDaysAfterFreezing_Accepts_Zero()
    {
        var household = Household.Create("Test", new FixedClock(Now));
        household.SetDefaultDueDaysAfterFreezing(0);
        Assert.Equal(0, household.DefaultDueDaysAfterFreezing);
    }

    // ── doubles ───────────────────────────────────────────────────────────────

    private sealed class FakeTenantContext(Guid? householdId) : ITenantContext
    {
        public Guid? HouseholdId { get; } = householdId;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class FakeHouseholdRepository : IHouseholdRepository
    {
        private readonly Dictionary<HouseholdId, Household> _byId = [];
        public int SaveChangesCalls { get; private set; }

        public FakeHouseholdRepository() { }

        public FakeHouseholdRepository(HouseholdId id, Household household) => _byId[id] = household;

        public Task<Household?> FindAsync(HouseholdId id, CancellationToken ct = default) =>
            Task.FromResult(_byId.GetValueOrDefault(id));

        public Task<IReadOnlyList<HouseholdId>> ListAllIdsAsync(CancellationToken ct = default) =>
            Task.FromResult((IReadOnlyList<HouseholdId>)_byId.Keys.ToList());

        public Task AddAsync(Household household, CancellationToken ct = default)
        {
            _byId[household.Id] = household;
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken ct = default)
        {
            SaveChangesCalls++;
            return Task.CompletedTask;
        }
    }
}
