using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Identity.Application;
using Plantry.Identity.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Unit.Identity.Application;

/// <summary>
/// L1 tests for <see cref="HouseholdExpiryDefaultsService"/> (the <see cref="IHouseholdExpiryDefaults"/>
/// read source that feeds Catalog's <c>ExpiryDefaultResolver</c> freeze/thaw fallback, plus the future
/// /Settings/Expiry write path) and the <see cref="Household.DefaultDueDaysAfterFreezing"/>/
/// <see cref="Household.DefaultDueDaysAfterThawing"/> settings (plantry-hh1f). Defaults are 90/3; the
/// write path requires a household in context and rejects negative day counts through the aggregate.
/// Mirrors <c>DisplayCurrencyServiceTests</c>'s shape.
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

    // ── Write (SetAfterFreezingAsync / SetAfterThawingAsync) ─────────────────

    [Fact(DisplayName = "SetAfterFreezingAsync persists a new value and the read source reflects it")]
    public async Task SetAfterFreezing_Persists_And_ReadsBack()
    {
        var repo = new FakeHouseholdRepository(HouseholdId.From(_household), SeededHousehold());
        var service = Service(repo, _household);

        var result = await service.SetAfterFreezingAsync(120);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, repo.SaveChangesCalls);
        Assert.Equal((120, 3), await service.GetAsync());
    }

    [Fact(DisplayName = "SetAfterThawingAsync persists a new value and the read source reflects it")]
    public async Task SetAfterThawing_Persists_And_ReadsBack()
    {
        var repo = new FakeHouseholdRepository(HouseholdId.From(_household), SeededHousehold());
        var service = Service(repo, _household);

        var result = await service.SetAfterThawingAsync(7);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, repo.SaveChangesCalls);
        Assert.Equal((90, 7), await service.GetAsync());
    }

    [Fact(DisplayName = "SetAfterFreezingAsync returns Unauthorized when there is no household in context")]
    public async Task SetAfterFreezing_Requires_Household()
    {
        var repo = new FakeHouseholdRepository();
        var result = await Service(repo, household: null).SetAfterFreezingAsync(120);

        Assert.True(result.IsFailure);
        Assert.Equal(Error.Unauthorized, result.Error);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    [Fact(DisplayName = "SetAfterThawingAsync returns NotFound when the household row is missing")]
    public async Task SetAfterThawing_NotFound_When_Row_Missing()
    {
        var repo = new FakeHouseholdRepository();
        var result = await Service(repo, _household).SetAfterThawingAsync(7);

        Assert.True(result.IsFailure);
        Assert.Equal(Error.NotFound, result.Error);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    [Fact(DisplayName = "SetAfterFreezingAsync rejects a negative value through the aggregate")]
    public async Task SetAfterFreezing_Rejects_Negative()
    {
        var repo = new FakeHouseholdRepository(HouseholdId.From(_household), SeededHousehold());
        var service = Service(repo, _household);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetAfterFreezingAsync(-1));
        Assert.Equal(0, repo.SaveChangesCalls);
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
