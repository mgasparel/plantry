using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;
using Xunit;

namespace Plantry.Tests.Web.MealPlanning;

/// <summary>
/// L4 fragment tests for persisted planning settings (plantry-so5.3).
///
/// Verifies the HTTP/OOB contract layer:
///   1. GET /MealPlan with a persisted budget → budget chip renders (settings are resolved from repo).
///   2. GET /MealPlan with no budget → budget chip still renders (shows "—"), no over-budget callout.
///   3. POST ?handler=SetPlanningSettings → returns HTML with grid + OOB bar elements.
///
/// Business-rule coverage (over-budget firing conditions) is covered by L1 PlanInsightsService unit
/// tests and L2 PlanningSettingsResolverTests. These L4 tests confirm the handler wiring and OOB
/// contract — not the full business rules.
/// </summary>
public sealed class PlanningSettingsFragmentTests
{
    private static readonly HtmlParser _parser = new();

    private static HttpClient MakeClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, WeekGridFixture.HouseholdId.ToString());
        return client;
    }

    // ── Budget chip is always present in the page ─────────────────────────────

    [Fact(DisplayName = "L4: GET /MealPlan — budget chip is present when budget is set in persisted settings")]
    public async Task Get_BudgetSet_BudgetChipPresent()
    {
        await using var factory = new BudgetSetPlanningSettingsFactory(budgetDecimal: 100m);
        var client = MakeClient(factory);

        var resp = await client.GetAsync("/MealPlan");
        resp.EnsureSuccessStatusCode();

        var html = await resp.Content.ReadAsStringAsync();
        var doc = await _parser.ParseDocumentAsync(html);

        // #plan-cost-chip must be present — it is always emitted by _PlanBarNav.cshtml
        var chip = doc.QuerySelector("#plan-cost-chip");
        Assert.NotNull(chip);
    }

    // ── No budget → budget chip still renders, no over-budget callout ─────────

    [Fact(DisplayName = "L4: GET /MealPlan — no budget set → page renders without over-budget callout")]
    public async Task Get_NoBudgetSet_NoOverBudgetInsight()
    {
        // Base WeekGridFragmentFactory has NullPlanningSettingsRepo (returns null settings → null budget)
        await using var factory = new WeekGridFragmentFactory();
        var client = MakeClient(factory);

        var resp = await client.GetAsync("/MealPlan");
        resp.EnsureSuccessStatusCode();

        var html = await resp.Content.ReadAsStringAsync();
        // No over-budget callout when no budget is configured
        Assert.DoesNotContain("OverBudget", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Over budget", html, StringComparison.OrdinalIgnoreCase);
    }

    // ── SetPlanningSettings POST handler OOB contract ─────────────────────────

    [Fact(DisplayName = "L4: POST ?handler=SetPlanningSettings returns HTML with week-grid, plan-bar-nav, and plan-cost-chip")]
    public async Task PostSetPlanningSettings_ReturnsGridAndBarOob()
    {
        await using var factory = new SetPlanningSettingsFactory();
        var client = MakeClient(factory);

        // GET first to obtain a valid antiforgery token
        var getResp = await client.GetAsync("/MealPlan");
        getResp.EnsureSuccessStatusCode();
        var getHtml = await getResp.Content.ReadAsStringAsync();

        var doc = await _parser.ParseDocumentAsync(getHtml);
        var tokenInput = doc.QuerySelector("input[name='__RequestVerificationToken']");
        Assert.NotNull(tokenInput);
        var token = tokenInput!.GetAttribute("value")!;

        // Derive the current Monday for the week param
        var today = DateOnly.FromDateTime(DateTime.Today);
        var daysFromMonday = ((int)today.DayOfWeek + 6) % 7; // ISO Monday
        var monday = today.AddDays(-daysFromMonday);

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["week"] = monday.ToString("yyyy-MM-dd"),
            ["budget"] = "150",
            ["wasteWeight"] = "60",
            ["costWeight"] = "20",
            ["varietyWeight"] = "20",
        });

        var resp = await client.PostAsync("/MealPlan?handler=SetPlanningSettings", form);
        resp.EnsureSuccessStatusCode();

        var html = await resp.Content.ReadAsStringAsync();

        // Response must contain the main grid (wkgrid) and both OOB bar elements.
        // Same OOB contract as GET ?handler=Grid — _GridWithBarNav emits _WeekGrid + _PlanBarNav.
        OobContract.AssertCarriesProjections(html, "plan-bar-nav", "plan-cost-chip", "plan-bar-autofill");
        Assert.Contains("wkgrid", html, StringComparison.OrdinalIgnoreCase);
    }
}

// ── Factories ─────────────────────────────────────────────────────────────────

/// <summary>
/// Factory for the "budget set" test. Seeds household default budget via a seeded settings repo.
/// </summary>
public sealed class BudgetSetPlanningSettingsFactory : WeekGridFragmentFactory
{
    private readonly decimal _budgetDecimal;

    public BudgetSetPlanningSettingsFactory(decimal budgetDecimal)
        => _budgetDecimal = budgetDecimal;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            var householdId = HouseholdId.From(WeekGridFixture.HouseholdId);
            var settings = HouseholdPlanningSettings.Create(householdId);
            settings.SetDefaults(Money.FromDecimal(_budgetDecimal, "USD"), null);

            services.RemoveAll<IHouseholdPlanningSettingsRepository>();
            services.AddSingleton<IHouseholdPlanningSettingsRepository>(
                new SeededPlanningSettingsRepo(settings));
        });
    }
}

/// <summary>
/// Factory for the SetPlanningSettings POST OOB contract test.
/// Uses mutable in-memory repos so the service can upsert the override.
/// </summary>
public sealed class SetPlanningSettingsFactory : WeekGridFragmentFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            // Replace null stubs with mutable in-memory stubs so ExecuteAsync can upsert
            services.RemoveAll<IHouseholdPlanningSettingsRepository>();
            services.AddSingleton<IHouseholdPlanningSettingsRepository>(new MutablePlanningSettingsRepo());

            services.RemoveAll<IWeekPlanningOverrideRepository>();
            services.AddSingleton<IWeekPlanningOverrideRepository>(new MutableWeekOverrideRepo());
        });
    }
}

// ── Repo stubs for L4 ─────────────────────────────────────────────────────────

internal sealed class SeededPlanningSettingsRepo(HouseholdPlanningSettings? seeded) : IHouseholdPlanningSettingsRepository
{
    public Task<HouseholdPlanningSettings?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        => Task.FromResult(seeded);

    public Task AddAsync(HouseholdPlanningSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class MutablePlanningSettingsRepo : IHouseholdPlanningSettingsRepository
{
    private HouseholdPlanningSettings? _stored;

    public Task<HouseholdPlanningSettings?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        => Task.FromResult(_stored);

    public Task AddAsync(HouseholdPlanningSettings settings, CancellationToken ct = default)
    {
        _stored = settings;
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class MutableWeekOverrideRepo : IWeekPlanningOverrideRepository
{
    private WeekPlanningOverride? _stored;

    public Task<WeekPlanningOverride?> FindAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
        => Task.FromResult(_stored);

    public Task AddAsync(WeekPlanningOverride weekOverride, CancellationToken ct = default)
    {
        _stored = weekOverride;
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}
