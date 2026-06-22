using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plantry.MealPlanning.Domain;
using Plantry.MealPlanning.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.MealPlanning;

/// <summary>
/// L3 integration tests: HouseholdPlanningSettings and WeekPlanningOverride round-trip through
/// the real PostgreSQL schema (meal_planning schema) under RLS.
///
/// Covers:
///   - Budget round-trips: persisted budget is loaded back correctly.
///   - Weights round-trips: persisted weights are loaded back correctly.
///   - Week-override isolation: override for weekA does not bleed into weekB.
///   - Cross-household isolation: household A cannot read household B's settings.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PlanningSettingsPersistenceTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _householdA;
    private HouseholdId _householdB;
    private static readonly DateOnly Week1 = new(2026, 6, 23);
    private static readonly DateOnly Week2 = new(2026, 6, 30);

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _householdA = HouseholdId.New();
        _householdB = HouseholdId.New();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── helpers ───────────────────────────────────────────────────────────────

    private DbContextOptions<MealPlanningDbContext> Options() =>
        new DbContextOptionsBuilder<MealPlanningDbContext>().UseNpgsql(db.ConnectionString).Options;

    private DbContextOptions<MealPlanningDbContext> AppUserOptions() =>
        new DbContextOptionsBuilder<MealPlanningDbContext>().UseNpgsql(db.AppUserConnectionString).Options;

    private MealPlanningDbContext NewDb(HouseholdId household, bool asAppUser = false)
    {
        var ctx = new MealPlanningDbContext(asAppUser ? AppUserOptions() : Options());
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }

    // ── Budget round-trip ─────────────────────────────────────────────────────

    [Fact(DisplayName = "L3: household default budget persists and round-trips through DbContext")]
    public async Task DefaultBudget_PersistsAndRoundTrips()
    {
        var budget = Money.FromDecimal(150m, "USD");

        await using (var writeDb = NewDb(_householdA))
        {
            var settings = HouseholdPlanningSettings.Create(_householdA);
            settings.SetDefaults(budget, null);
            await writeDb.HouseholdPlanningSettings.AddAsync(settings);
            await writeDb.SaveChangesAsync();
        }

        await using var readDb = NewDb(_householdA);
        var reloaded = await readDb.HouseholdPlanningSettings.FirstOrDefaultAsync();

        Assert.NotNull(reloaded);
        Assert.NotNull(reloaded!.DefaultWeeklyBudget);
        Assert.Equal(budget.MinorUnits, reloaded.DefaultWeeklyBudget!.MinorUnits);
        Assert.Equal("USD", reloaded.DefaultWeeklyBudget.Currency);
        Assert.Null(reloaded.DefaultPlanningWeights);
    }

    // ── Weights round-trip ────────────────────────────────────────────────────

    [Fact(DisplayName = "L3: household default weights persist and round-trip through DbContext")]
    public async Task DefaultWeights_PersistsAndRoundTrips()
    {
        var weights = new PlanningWeights(50, 30, 20);

        await using (var writeDb = NewDb(_householdA))
        {
            var settings = HouseholdPlanningSettings.Create(_householdA);
            settings.SetDefaults(null, weights);
            await writeDb.HouseholdPlanningSettings.AddAsync(settings);
            await writeDb.SaveChangesAsync();
        }

        await using var readDb = NewDb(_householdA);
        var reloaded = await readDb.HouseholdPlanningSettings.FirstOrDefaultAsync();

        Assert.NotNull(reloaded);
        Assert.NotNull(reloaded!.DefaultPlanningWeights);
        Assert.Equal(50, reloaded.DefaultPlanningWeights!.Waste);
        Assert.Equal(30, reloaded.DefaultPlanningWeights.Cost);
        Assert.Equal(20, reloaded.DefaultPlanningWeights.Variety);
    }

    // ── Week override isolation ───────────────────────────────────────────────

    [Fact(DisplayName = "L3: week override for week1 is not visible for week2")]
    public async Task WeekOverride_IsolatedToItsWeek()
    {
        var budget = Money.FromDecimal(75m, "USD");

        await using (var writeDb = NewDb(_householdA))
        {
            var weekOverride = WeekPlanningOverride.Create(_householdA, Week1);
            weekOverride.Set(budget, null);
            await writeDb.WeekPlanningOverrides.AddAsync(weekOverride);
            await writeDb.SaveChangesAsync();
        }

        await using var readDb = NewDb(_householdA);

        // Week1 override exists
        var week1Override = await readDb.WeekPlanningOverrides
            .FirstOrDefaultAsync(o => o.WeekStart == Week1);
        Assert.NotNull(week1Override);
        Assert.Equal(budget.MinorUnits, week1Override!.BudgetOverride!.MinorUnits);

        // Week2 has no override
        var week2Override = await readDb.WeekPlanningOverrides
            .FirstOrDefaultAsync(o => o.WeekStart == Week2);
        Assert.Null(week2Override);
    }

    // ── Cross-household isolation ─────────────────────────────────────────────

    [Fact(DisplayName = "L3: EF query filter prevents household A from reading household B's settings")]
    public async Task EfFilter_CrossHouseholdIsolation_Settings()
    {
        var budgetA = Money.FromDecimal(100m, "USD");
        var budgetB = Money.FromDecimal(200m, "USD");

        await using (var writeA = NewDb(_householdA))
        {
            var settingsA = HouseholdPlanningSettings.Create(_householdA);
            settingsA.SetDefaults(budgetA, null);
            await writeA.HouseholdPlanningSettings.AddAsync(settingsA);
            await writeA.SaveChangesAsync();
        }

        await using (var writeB = NewDb(_householdB))
        {
            var settingsB = HouseholdPlanningSettings.Create(_householdB);
            settingsB.SetDefaults(budgetB, null);
            await writeB.HouseholdPlanningSettings.AddAsync(settingsB);
            await writeB.SaveChangesAsync();
        }

        await using var readAsA = NewDb(_householdA);
        var settingsForA = await readAsA.HouseholdPlanningSettings.ToListAsync();

        Assert.Single(settingsForA);
        Assert.Equal(budgetA.MinorUnits, settingsForA[0].DefaultWeeklyBudget!.MinorUnits);
    }

    [Fact(DisplayName = "L3: RLS prevents app_user from reading other household's settings via raw connection")]
    public async Task RlsPolicy_CrossHouseholdIsolation_Settings()
    {
        var budgetA = Money.FromDecimal(100m, "USD");
        var budgetB = Money.FromDecimal(200m, "USD");

        // Seed two households as superuser
        await using (var writeA = NewDb(_householdA))
        {
            var settingsA = HouseholdPlanningSettings.Create(_householdA);
            settingsA.SetDefaults(budgetA, null);
            await writeA.HouseholdPlanningSettings.AddAsync(settingsA);
            await writeA.SaveChangesAsync();
        }

        await using (var writeB = NewDb(_householdB))
        {
            var settingsB = HouseholdPlanningSettings.Create(_householdB);
            settingsB.SetDefaults(budgetB, null);
            await writeB.HouseholdPlanningSettings.AddAsync(settingsB);
            await writeB.SaveChangesAsync();
        }

        // Read as app_user with household A's context — RLS policy must filter out B's row
        await using var conn = new NpgsqlConnection(db.AppUserConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SET app.household_id = '{_householdA.Value}'; SELECT COUNT(*) FROM meal_planning.household_planning_settings";
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(1, count);
    }

    [Fact(DisplayName = "L3: EF query filter prevents household A from reading household B's week overrides")]
    public async Task EfFilter_CrossHouseholdIsolation_WeekOverrides()
    {
        var budgetA = Money.FromDecimal(100m, "USD");
        var budgetB = Money.FromDecimal(200m, "USD");

        await using (var writeA = NewDb(_householdA))
        {
            var overrideA = WeekPlanningOverride.Create(_householdA, Week1);
            overrideA.Set(budgetA, null);
            await writeA.WeekPlanningOverrides.AddAsync(overrideA);
            await writeA.SaveChangesAsync();
        }

        await using (var writeB = NewDb(_householdB))
        {
            var overrideB = WeekPlanningOverride.Create(_householdB, Week1);
            overrideB.Set(budgetB, null);
            await writeB.WeekPlanningOverrides.AddAsync(overrideB);
            await writeB.SaveChangesAsync();
        }

        await using var readAsA = NewDb(_householdA);
        var overridesForA = await readAsA.WeekPlanningOverrides.ToListAsync();

        Assert.Single(overridesForA);
        Assert.Equal(budgetA.MinorUnits, overridesForA[0].BudgetOverride!.MinorUnits);
    }
}
