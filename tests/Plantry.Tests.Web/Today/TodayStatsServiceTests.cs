using Plantry.Pantry.Application;
using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Web.Pages.Today;
using Xunit;

namespace Plantry.Tests.Web.Today;

/// <summary>
/// L1 unit tests for <see cref="TodayStatsService.BuildRotatingFact"/> (plantry-h9z9) — the pure,
/// deterministic-by-day rotation logic behind the Today "did you know" tile. No DB, no DI: exercises
/// the static method directly with varying <c>DateOnly</c>/streak/waste-count/tenure inputs.
/// </summary>
public sealed class TodayStatsServiceTests
{
    private static readonly DateOnly Today = new(2026, 6, 18);

    [Fact(DisplayName = "Same day + same inputs → same fact (deterministic, not random)")]
    public void SameDayAndInputs_ReturnsSameFact()
    {
        var first = TodayStatsService.BuildRotatingFact(Today, streakWeeks: 3, wasteCountLast30Days: 2, tenureDays: 40);
        var second = TodayStatsService.BuildRotatingFact(Today, streakWeeks: 3, wasteCountLast30Days: 2, tenureDays: 40);

        Assert.Equal(first, second);
    }

    [Fact(DisplayName = "Zero-week streak is skipped — the rotation never lands on a 0-week streak sentence")]
    public void ZeroWeekStreak_NeverProducesAStreakSentence()
    {
        // Sweep every day-of-year offset so every rotation start index gets exercised at least once.
        for (var offset = 0; offset < 7; offset++)
        {
            var day = Today.AddDays(offset);
            var fact = TodayStatsService.BuildRotatingFact(day, streakWeeks: 0, wasteCountLast30Days: 5, tenureDays: 10);

            Assert.DoesNotContain("week", fact, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact(DisplayName = "Zero waste count still produces a positive fact, not a null/blank one")]
    public void ZeroWasteCount_ProducesPositiveFact()
    {
        // Force the rotation onto the waste-trend candidate (index 0) by using a day whose DayNumber % 3 == 0.
        var day = FindDayWithRotationIndex(0);

        var fact = TodayStatsService.BuildRotatingFact(day, streakWeeks: 0, wasteCountLast30Days: 0, tenureDays: 10);

        Assert.Contains("nothing", fact, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Non-zero waste count is reported by count in the waste-trend fact")]
    public void NonZeroWasteCount_ReportsCount()
    {
        var day = FindDayWithRotationIndex(0);

        var fact = TodayStatsService.BuildRotatingFact(day, streakWeeks: 0, wasteCountLast30Days: 4, tenureDays: 10);

        Assert.Contains("4", fact, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Planning-streak candidate reports the streak count when it wins the rotation")]
    public void StreakCandidate_ReportsStreakCount()
    {
        var day = FindDayWithRotationIndex(1);

        var fact = TodayStatsService.BuildRotatingFact(day, streakWeeks: 7, wasteCountLast30Days: 0, tenureDays: 10);

        Assert.Contains("7", fact, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Tenure candidate reports the day count when it wins the rotation")]
    public void TenureCandidate_ReportsTenureDays()
    {
        var day = FindDayWithRotationIndex(2);

        var fact = TodayStatsService.BuildRotatingFact(day, streakWeeks: 0, wasteCountLast30Days: 0, tenureDays: 99);

        Assert.Contains("99", fact, StringComparison.Ordinal);
    }

    /// <summary>Finds the smallest date on/after <see cref="Today"/> whose rotation start index
    /// (<c>DayNumber % 3</c>) equals <paramref name="index"/> — lets each test target a specific
    /// rotation candidate without depending on <see cref="Today"/>'s own arbitrary offset.</summary>
    private static DateOnly FindDayWithRotationIndex(int index)
    {
        for (var offset = 0; offset < 3; offset++)
        {
            var day = Today.AddDays(offset);
            if (day.DayNumber % 3 == index)
                return day;
        }
        throw new InvalidOperationException("Unreachable: one of the next 3 days always matches.");
    }
}

/// <summary>
/// L2 unit tests for <see cref="TodayStatsService.BuildAsync"/> (plantry-h9z9) — the async composition
/// that turns a streak query + a waste-journal reader + a household-created timestamp into
/// <see cref="TodayStatsVm.StreakChips"/>. Uses in-memory fakes (no DB) for both dependencies.
/// </summary>
public sealed class TodayStatsServiceBuildAsyncTests
{
    private static readonly HouseholdId HouseholdId = HouseholdId.New();
    private static readonly DateOnly Today = new(2026, 6, 18); // a Thursday
    private static readonly IClock Clock = new FixedUtcClock(
        new DateTimeOffset(2026, 6, 18, 9, 0, 0, TimeSpan.Zero));

    private static TodayStatsService BuildService(
        IWasteJournalReader? wasteReader = null, IReadOnlyCollection<DateOnly>? plannedWeeks = null) =>
        new(
            wasteReader ?? new StubWasteJournalReader(count: 0, lastDiscard: null),
            new MealPlanStreakQuery(new FixedWeeksMealPlanRepository(plannedWeeks ?? [])),
            Clock);

    [Fact(DisplayName = "Zero streak and no discard history → no streak chips")]
    public async Task NoStreakNoDiscardHistory_NoChips()
    {
        var service = BuildService();

        var vm = await service.BuildAsync(HouseholdId, Clock.UtcNow, Today);

        Assert.Empty(vm.StreakChips);
    }

    [Fact(DisplayName = "A 3-week streak produces a chip reading '3-week' / 'planning streak'")]
    public async Task ThreeWeekStreak_ProducesStreakChip()
    {
        var week0 = MealPlan.NormalizeToMonday(Today);
        var plannedWeeks = new[] { week0, week0.AddDays(-7), week0.AddDays(-14) };
        var service = BuildService(plannedWeeks: plannedWeeks);

        var vm = await service.BuildAsync(HouseholdId, Clock.UtcNow, Today);

        var chip = Assert.Single(vm.StreakChips, c => c.Label == "planning streak");
        Assert.Equal("3-week", chip.Value);
    }

    [Fact(DisplayName = "A discard exactly 1 day ago produces a singular 'day since anything expired' chip")]
    public async Task DiscardOneDayAgo_ProducesSingularChip()
    {
        var lastDiscard = Clock.UtcNow.AddDays(-1);
        var service = BuildService(wasteReader: new StubWasteJournalReader(count: 0, lastDiscard));

        var vm = await service.BuildAsync(HouseholdId, Clock.UtcNow, Today);

        var chip = Assert.Single(vm.StreakChips, c => c.Label.Contains("since anything expired"));
        Assert.Equal("1", chip.Value);
        Assert.Equal("day since anything expired", chip.Label);
    }

    [Fact(DisplayName = "A discard 5 days ago produces a plural 'days since anything expired' chip")]
    public async Task DiscardFiveDaysAgo_ProducesPluralChip()
    {
        var lastDiscard = Clock.UtcNow.AddDays(-5);
        var service = BuildService(wasteReader: new StubWasteJournalReader(count: 0, lastDiscard));

        var vm = await service.BuildAsync(HouseholdId, Clock.UtcNow, Today);

        var chip = Assert.Single(vm.StreakChips, c => c.Label.Contains("since anything expired"));
        Assert.Equal("5", chip.Value);
        Assert.Equal("days since anything expired", chip.Label);
    }

    [Fact(DisplayName = "A discard timestamped after 'today' clamps to 0, never a negative day count")]
    public async Task DiscardAfterToday_ClampsToZero()
    {
        var lastDiscard = Clock.UtcNow.AddDays(3); // in the future relative to Today
        var service = BuildService(wasteReader: new StubWasteJournalReader(count: 0, lastDiscard));

        var vm = await service.BuildAsync(HouseholdId, Clock.UtcNow, Today);

        var chip = Assert.Single(vm.StreakChips, c => c.Label.Contains("since anything expired"));
        Assert.Equal("0", chip.Value);
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class FixedUtcClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class StubWasteJournalReader(int count, DateTimeOffset? lastDiscard) : IWasteJournalReader
    {
        public Task<int> CountDiscardedSinceAsync(DateTimeOffset since, CancellationToken ct = default) =>
            Task.FromResult(count);
        public Task<DateTimeOffset?> MostRecentDiscardAsync(CancellationToken ct = default) =>
            Task.FromResult(lastDiscard);
    }

    /// <summary>Returns a planned (non-empty) MealPlan for exactly the given week-starts; every other
    /// week resolves to null (unplanned) — enough for <see cref="MealPlanStreakQuery"/>'s default
    /// per-week fallback (this fake doesn't override <c>PlannedWeekStartsBeforeAsync</c>).</summary>
    private sealed class FixedWeeksMealPlanRepository(IReadOnlyCollection<DateOnly> plannedWeeks) : IMealPlanRepository
    {
        private readonly HashSet<DateOnly> _planned = [.. plannedWeeks];

        public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
        {
            if (!_planned.Contains(weekStart))
                return Task.FromResult<MealPlan?>(null);

            var plan = MealPlan.Start(householdId, weekStart, SystemClock.Instance);
            plan.AssignMeal(
                weekStart, MealSlotId.New(), [new DishSpec(DishKind.Recipe, Guid.NewGuid(), 2)],
                null, "test", Guid.NewGuid(), SystemClock.Instance);
            return Task.FromResult<MealPlan?>(plan);
        }

        public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default) =>
            throw new NotSupportedException("Not used by TodayStatsService.");

        public Task<IReadOnlyDictionary<Guid, PlannedMealSlotInfo>> FindSlotLabelsAsync(
            IReadOnlyList<Guid> plannedMealIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, PlannedMealSlotInfo>>(new Dictionary<Guid, PlannedMealSlotInfo>());

        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
