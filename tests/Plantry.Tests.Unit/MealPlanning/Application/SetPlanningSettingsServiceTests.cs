using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Xunit;

namespace Plantry.Tests.Unit.MealPlanning.Application;

/// <summary>
/// L2 unit tests for <see cref="SetPlanningSettingsService"/> — the application service
/// that upserts per-week overrides (or the household default when no week is specified).
/// Uses in-memory fake repositories — no DB.
/// </summary>
public sealed class SetPlanningSettingsServiceTests
{
    private static readonly HouseholdId HouseholdId = Plantry.SharedKernel.HouseholdId.New();
    private static readonly DateOnly Week = new(2026, 6, 23);
    private static readonly Money Budget = Money.FromDecimal(100m, "USD");
    private static readonly PlanningWeights Weights = new(60, 20, 20);

    private static SetPlanningSettingsService BuildService(
        out FakePlanningSettingsRepo settingsRepo,
        out FakeWeekOverrideRepo overrideRepo)
    {
        settingsRepo = new FakePlanningSettingsRepo();
        overrideRepo = new FakeWeekOverrideRepo();
        return new SetPlanningSettingsService(settingsRepo, overrideRepo);
    }

    // ── Per-week override: create ─────────────────────────────────────────────

    [Fact(DisplayName = "ExecuteAsync: creates new week override when none exists")]
    public async Task Execute_CreatesOverride_WhenNoneExists()
    {
        var svc = BuildService(out var settingsRepo, out var overrideRepo);

        await svc.ExecuteAsync(HouseholdId, Week, Budget, Weights);

        Assert.NotNull(overrideRepo.Stored);
        Assert.Equal(Week, overrideRepo.Stored!.WeekStart);
        Assert.Equal(Budget.MinorUnits, overrideRepo.Stored.BudgetOverride!.MinorUnits);
        Assert.Equal(Weights.Waste, overrideRepo.Stored.WeightsOverride!.Waste);
    }

    [Fact(DisplayName = "ExecuteAsync: returns resolved values after creating override")]
    public async Task Execute_ReturnsResolvedValues_AfterCreate()
    {
        var svc = BuildService(out _, out _);

        var (budget, weights) = await svc.ExecuteAsync(HouseholdId, Week, Budget, Weights);

        Assert.Equal(Budget.MinorUnits, budget!.MinorUnits);
        Assert.Equal(Weights.Waste, weights!.Waste);
    }

    // ── Per-week override: update ─────────────────────────────────────────────

    [Fact(DisplayName = "ExecuteAsync: updates existing week override")]
    public async Task Execute_UpdatesOverride_WhenExists()
    {
        var svc = BuildService(out var settingsRepo, out var overrideRepo);
        var existing = WeekPlanningOverride.Create(HouseholdId, Week);
        existing.Set(Money.FromDecimal(80m, "USD"), null);
        overrideRepo.Stored = existing;

        var newBudget = Money.FromDecimal(120m, "USD");
        await svc.ExecuteAsync(HouseholdId, Week, newBudget, Weights);

        Assert.Equal(120_00L, overrideRepo.Stored!.BudgetOverride!.MinorUnits);
        Assert.Equal(Weights.Waste, overrideRepo.Stored.WeightsOverride!.Waste);
    }

    // ── Household default ─────────────────────────────────────────────────────

    [Fact(DisplayName = "ExecuteAsync: creates new household settings when weekStart is null")]
    public async Task Execute_CreatesHouseholdSettings_WhenWeekStartIsNull()
    {
        var svc = BuildService(out var settingsRepo, out _);

        await svc.ExecuteAsync(HouseholdId, weekStart: null, Budget, Weights);

        Assert.NotNull(settingsRepo.Stored);
        Assert.Equal(Budget.MinorUnits, settingsRepo.Stored!.DefaultWeeklyBudget!.MinorUnits);
        Assert.Equal(Weights.Waste, settingsRepo.Stored.DefaultPlanningWeights!.Waste);
    }

    [Fact(DisplayName = "ExecuteAsync: updates existing household settings when weekStart is null")]
    public async Task Execute_UpdatesHouseholdSettings_WhenExists()
    {
        var svc = BuildService(out var settingsRepo, out _);
        var existing = HouseholdPlanningSettings.Create(HouseholdId);
        existing.SetDefaults(Money.FromDecimal(80m, "USD"), null);
        settingsRepo.Stored = existing;

        var newBudget = Money.FromDecimal(200m, "USD");
        await svc.ExecuteAsync(HouseholdId, weekStart: null, newBudget, null);

        Assert.Equal(200_00L, settingsRepo.Stored!.DefaultWeeklyBudget!.MinorUnits);
        Assert.Null(settingsRepo.Stored.DefaultPlanningWeights);
    }

    // ── Fake repos ────────────────────────────────────────────────────────────

    private sealed class FakePlanningSettingsRepo : IHouseholdPlanningSettingsRepository
    {
        public HouseholdPlanningSettings? Stored { get; set; }

        public Task<HouseholdPlanningSettings?> FindByHouseholdAsync(HouseholdId id, CancellationToken ct = default)
            => Task.FromResult(Stored);

        public Task AddAsync(HouseholdPlanningSettings settings, CancellationToken ct = default)
        {
            Stored = settings;
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeWeekOverrideRepo : IWeekPlanningOverrideRepository
    {
        public WeekPlanningOverride? Stored { get; set; }

        public Task<WeekPlanningOverride?> FindAsync(HouseholdId id, DateOnly weekStart, CancellationToken ct = default)
            => Task.FromResult(Stored);

        public Task AddAsync(WeekPlanningOverride weekOverride, CancellationToken ct = default)
        {
            Stored = weekOverride;
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
