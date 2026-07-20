using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Identity.Application;
using Plantry.Identity.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Unit.Identity.Application;

/// <summary>
/// L1 tests for <see cref="DisplayCurrencyService"/> (the <see cref="IDisplayCurrency"/> read source +
/// the /Settings/Currency write path) and the <see cref="Household.DisplayCurrency"/> setting
/// (plantry-2x6e.1). Default is USD; the write path requires a household in context and normalizes /
/// validates the code through the aggregate.
/// </summary>
public sealed class DisplayCurrencyServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid _household = Guid.Parse("cccccccc-0002-0000-0000-000000000001");

    private static DisplayCurrencyService Service(FakeHouseholdRepository repo, Guid? household) =>
        new(repo, new FakeTenantContext(household), NullLogger<DisplayCurrencyService>.Instance);

    private Household SeededHousehold(string? currency = null)
    {
        var household = Household.Create("Test", new FixedClock(Now));
        if (currency is not null) household.SetDisplayCurrency(currency);
        return household;
    }

    // ── Read (GetAsync) ───────────────────────────────────────────────────────

    [Fact(DisplayName = "GetAsync defaults to USD when there is no household in context")]
    public async Task Get_Defaults_Usd_When_No_Household()
    {
        var currency = await Service(new FakeHouseholdRepository(), household: null).GetAsync();
        Assert.Equal("USD", currency);
        Assert.Equal("USD", DisplayCurrencyService.Default);
    }

    [Fact(DisplayName = "GetAsync defaults to USD when the household row is not found")]
    public async Task Get_Defaults_Usd_When_Row_Missing()
    {
        var currency = await Service(new FakeHouseholdRepository(), _household).GetAsync();
        Assert.Equal("USD", currency);
    }

    [Fact(DisplayName = "GetAsync reflects the household's persisted currency")]
    public async Task Get_Reflects_Household_Currency()
    {
        var repo = new FakeHouseholdRepository(HouseholdId.From(_household), SeededHousehold("EUR"));
        Assert.Equal("EUR", await Service(repo, _household).GetAsync());
    }

    // ── Write (SetAsync) ──────────────────────────────────────────────────────

    [Fact(DisplayName = "SetAsync persists a new currency and the read source reflects it")]
    public async Task Set_Persists_And_ReadsBack()
    {
        var repo = new FakeHouseholdRepository(HouseholdId.From(_household), SeededHousehold());
        var service = Service(repo, _household);

        var result = await service.SetAsync("GBP");

        Assert.True(result.IsSuccess);
        Assert.Equal(1, repo.SaveChangesCalls);
        Assert.Equal("GBP", await service.GetAsync());
    }

    [Fact(DisplayName = "SetAsync normalizes case and whitespace through the aggregate")]
    public async Task Set_Normalizes_Input()
    {
        var repo = new FakeHouseholdRepository(HouseholdId.From(_household), SeededHousehold());
        var service = Service(repo, _household);

        Assert.True((await service.SetAsync("  cad ")).IsSuccess);
        Assert.Equal("CAD", await service.GetAsync());
    }

    [Fact(DisplayName = "SetAsync returns Unauthorized when there is no household in context")]
    public async Task Set_Requires_Household()
    {
        var repo = new FakeHouseholdRepository();
        var result = await Service(repo, household: null).SetAsync("EUR");

        Assert.True(result.IsFailure);
        Assert.Equal(Error.Unauthorized, result.Error);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    [Fact(DisplayName = "SetAsync returns NotFound when the household row is missing")]
    public async Task Set_NotFound_When_Row_Missing()
    {
        var repo = new FakeHouseholdRepository();
        var result = await Service(repo, _household).SetAsync("EUR");

        Assert.True(result.IsFailure);
        Assert.Equal(Error.NotFound, result.Error);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    // ── Aggregate invariant ───────────────────────────────────────────────────

    [Fact(DisplayName = "A freshly created household displays in USD by default")]
    public void Create_Defaults_Currency_Usd()
    {
        var household = Household.Create("Test", new FixedClock(Now));
        Assert.Equal("USD", household.DisplayCurrency);
    }

    [Theory]
    [InlineData("eur", "EUR")]
    [InlineData("  gbp  ", "GBP")]
    [InlineData("CaD", "CAD")]
    public void SetDisplayCurrency_Normalizes(string input, string expected)
    {
        var household = Household.Create("Test", new FixedClock(Now));
        household.SetDisplayCurrency(input);
        Assert.Equal(expected, household.DisplayCurrency);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData("US1")]
    [InlineData("U$D")]
    public void SetDisplayCurrency_Rejects_Invalid(string input)
    {
        var household = Household.Create("Test", new FixedClock(Now));
        Assert.Throws<ArgumentException>(() => household.SetDisplayCurrency(input));
    }

    [Fact(DisplayName = "SetDisplayCurrency rejects null")]
    public void SetDisplayCurrency_Rejects_Null()
    {
        var household = Household.Create("Test", new FixedClock(Now));
        Assert.Throws<ArgumentNullException>(() => household.SetDisplayCurrency(null!));
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
