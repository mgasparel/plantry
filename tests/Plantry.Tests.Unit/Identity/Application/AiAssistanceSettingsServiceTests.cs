using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Identity.Application;
using Plantry.Identity.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Unit.Identity.Application;

/// <summary>
/// L1 tests for <see cref="AiAssistanceSettingsService"/> (the <see cref="IAiAssistanceGate"/> read
/// source + the /Settings/Ai write path) and the <see cref="Household.AiAssistanceEnabled"/> switch —
/// the household-wide "AI assistance" gate (plantry-qll2.1). Default is ON; the write path requires a
/// household in context.
/// </summary>
public sealed class AiAssistanceSettingsServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid _household = Guid.Parse("cccccccc-0001-0000-0000-000000000001");

    private static AiAssistanceSettingsService Service(FakeHouseholdRepository repo, Guid? household) =>
        new(repo, new FakeTenantContext(household), NullLogger<AiAssistanceSettingsService>.Instance);

    private Household SeededHousehold(bool enabled)
    {
        var household = Household.Create("Test", new FixedClock(Now));
        // A freshly created household defaults to enabled; only toggle when the test wants OFF.
        if (!enabled) household.SetAiAssistanceEnabled(false);
        return household;
    }

    // ── Gate read (IsEnabledAsync) ────────────────────────────────────────────

    [Fact(DisplayName = "IsEnabled defaults to true when there is no household in context")]
    public async Task IsEnabled_Defaults_True_When_No_Household()
    {
        var enabled = await Service(new FakeHouseholdRepository(), household: null).IsEnabledAsync();
        Assert.True(enabled);
        Assert.True(AiAssistanceSettingsService.DefaultEnabled);
    }

    [Fact(DisplayName = "IsEnabled defaults to true when the household row is not found")]
    public async Task IsEnabled_Defaults_True_When_Row_Missing()
    {
        var enabled = await Service(new FakeHouseholdRepository(), _household).IsEnabledAsync();
        Assert.True(enabled);
    }

    [Fact(DisplayName = "IsEnabled reflects a household whose switch is ON")]
    public async Task IsEnabled_True_When_Household_On()
    {
        var repo = new FakeHouseholdRepository(HouseholdId.From(_household), SeededHousehold(enabled: true));
        Assert.True(await Service(repo, _household).IsEnabledAsync());
    }

    [Fact(DisplayName = "IsEnabled reflects a household whose switch is OFF")]
    public async Task IsEnabled_False_When_Household_Off()
    {
        var repo = new FakeHouseholdRepository(HouseholdId.From(_household), SeededHousehold(enabled: false));
        Assert.False(await Service(repo, _household).IsEnabledAsync());
    }

    // ── Write (SetEnabledAsync) ───────────────────────────────────────────────

    [Fact(DisplayName = "SetEnabled(false) persists OFF and the gate reads it back")]
    public async Task SetEnabled_Off_Persists_And_ReadsBack()
    {
        var repo = new FakeHouseholdRepository(HouseholdId.From(_household), SeededHousehold(enabled: true));
        var service = Service(repo, _household);

        var result = await service.SetEnabledAsync(false);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, repo.SaveChangesCalls);
        Assert.False(await service.IsEnabledAsync());
    }

    [Fact(DisplayName = "SetEnabled(true) re-enables a previously disabled household")]
    public async Task SetEnabled_On_ReEnables()
    {
        var repo = new FakeHouseholdRepository(HouseholdId.From(_household), SeededHousehold(enabled: false));
        var service = Service(repo, _household);

        var result = await service.SetEnabledAsync(true);

        Assert.True(result.IsSuccess);
        Assert.True(await service.IsEnabledAsync());
    }

    [Fact(DisplayName = "SetEnabled returns Unauthorized when there is no household in context")]
    public async Task SetEnabled_Requires_Household()
    {
        var repo = new FakeHouseholdRepository();
        var result = await Service(repo, household: null).SetEnabledAsync(false);

        Assert.True(result.IsFailure);
        Assert.Equal(Error.Unauthorized, result.Error);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    [Fact(DisplayName = "SetEnabled returns NotFound when the household row is missing")]
    public async Task SetEnabled_NotFound_When_Row_Missing()
    {
        var repo = new FakeHouseholdRepository();
        var result = await Service(repo, _household).SetEnabledAsync(false);

        Assert.True(result.IsFailure);
        Assert.Equal(Error.NotFound, result.Error);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    // ── Aggregate invariant ───────────────────────────────────────────────────

    [Fact(DisplayName = "A freshly created household has AI assistance ON by default")]
    public void Create_Defaults_AiAssistance_On()
    {
        var household = Household.Create("Test", new FixedClock(Now));
        Assert.True(household.AiAssistanceEnabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetAiAssistanceEnabled_Toggles(bool enabled)
    {
        var household = Household.Create("Test", new FixedClock(Now));
        household.SetAiAssistanceEnabled(enabled);
        Assert.Equal(enabled, household.AiAssistanceEnabled);
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
