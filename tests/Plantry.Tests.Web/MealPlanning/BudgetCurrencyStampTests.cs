using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Identity.Application;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.Tests.Web.Infrastructure;
using Xunit;

namespace Plantry.Tests.Web.MealPlanning;

/// <summary>
/// L4 tests proving the two budget write sites stamp the household's display currency (plantry-2x6e.1)
/// instead of the old hardcoded "USD":
///   1. /MealPlan ?handler=SetPlanningSettings — the per-week override budget.
///   2. /Settings/MealPlanning ?handler=SetMealPlanningDefaults — the household-default budget.
///
/// Both resolve the currency via <see cref="IDisplayCurrency"/>, faked here to "EUR"; capturing repos
/// hold the persisted <see cref="Money"/> so the test can assert its <see cref="Money.Currency"/>.
/// </summary>
public sealed class BudgetCurrencyStampTests
{
    private static readonly HtmlParser _parser = new();

    private static HttpClient MakeClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, WeekGridFixture.HouseholdId.ToString());
        return client;
    }

    private static async Task<string> AntiforgeryTokenAsync(HttpClient client, string path)
    {
        var resp = await client.GetAsync(path);
        resp.EnsureSuccessStatusCode();
        var doc = await _parser.ParseDocumentAsync(await resp.Content.ReadAsStringAsync());
        var input = doc.QuerySelector("input[name='__RequestVerificationToken']");
        Assert.NotNull(input);
        return input!.GetAttribute("value")!;
    }

    // ── 1. Per-week override budget (MealPlan/Index) ──────────────────────────

    [Fact(DisplayName = "L4: /MealPlan budget save stamps the household display currency, not USD")]
    public async Task WeekBudgetSave_StampsHouseholdCurrency()
    {
        await using var factory = new CurrencyStampFactory("EUR");
        var client = MakeClient(factory);

        var token = await AntiforgeryTokenAsync(client, "/MealPlan");

        var today = DateOnly.FromDateTime(DateTime.Today);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["week"] = monday.ToString("yyyy-MM-dd"),
            ["budget"] = "150",
        });

        var resp = await client.PostAsync("/MealPlan?handler=SetPlanningSettings", form);
        resp.EnsureSuccessStatusCode();

        var captured = factory.OverrideRepo.Stored;
        Assert.NotNull(captured);
        Assert.NotNull(captured!.BudgetOverride);
        Assert.Equal("EUR", captured.BudgetOverride!.Currency);
    }

    // ── 2. Household-default budget (Settings/MealPlanning) ───────────────────

    [Fact(DisplayName = "L4: /Settings/MealPlanning budget save stamps the household display currency, not USD")]
    public async Task DefaultBudgetSave_StampsHouseholdCurrency()
    {
        await using var factory = new CurrencyStampFactory("EUR");
        var client = MakeClient(factory);

        var token = await AntiforgeryTokenAsync(client, "/Settings/MealPlanning");

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["budget"] = "200",
        });

        var resp = await client.PostAsync("/Settings/MealPlanning?handler=SetMealPlanningDefaults", form);
        resp.EnsureSuccessStatusCode();

        var captured = factory.SettingsRepo.Stored;
        Assert.NotNull(captured);
        Assert.NotNull(captured!.DefaultWeeklyBudget);
        Assert.Equal("EUR", captured.DefaultWeeklyBudget!.Currency);
    }

    // ── factory ──────────────────────────────────────────────────────────────

    private sealed class CurrencyStampFactory(string currency) : WeekGridFragmentFactory
    {
        public CapturingPlanningSettingsRepo SettingsRepo { get; } = new();
        public CapturingWeekOverrideRepo OverrideRepo { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureTestServices(services =>
            {
                services.AddFakeExpiringSoonHorizon();

                // Household displays in a non-USD currency — the write sites must stamp this.
                services.AddFakeDisplayCurrency(currency);

                services.RemoveAll<IHouseholdPlanningSettingsRepository>();
                services.AddSingleton<IHouseholdPlanningSettingsRepository>(SettingsRepo);

                services.RemoveAll<IWeekPlanningOverrideRepository>();
                services.AddSingleton<IWeekPlanningOverrideRepository>(OverrideRepo);
            });
        }
    }

    internal sealed class CapturingPlanningSettingsRepo : IHouseholdPlanningSettingsRepository
    {
        public HouseholdPlanningSettings? Stored { get; private set; }

        public Task<HouseholdPlanningSettings?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
            => Task.FromResult(Stored);

        public Task AddAsync(HouseholdPlanningSettings settings, CancellationToken ct = default)
        {
            Stored = settings;
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    internal sealed class CapturingWeekOverrideRepo : IWeekPlanningOverrideRepository
    {
        public WeekPlanningOverride? Stored { get; private set; }

        public Task<WeekPlanningOverride?> FindAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
            => Task.FromResult(Stored);

        public Task AddAsync(WeekPlanningOverride weekOverride, CancellationToken ct = default)
        {
            Stored = weekOverride;
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
