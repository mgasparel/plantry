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
using Plantry.Web.MealPlanning;
using Xunit;

namespace Plantry.Tests.Web.MealPlanning;

/// <summary>
/// ADR-013 OOB-contract tests for the plan-bar nav projections (plantry-khw / plantry-pg6).
///
/// The .plan-bar lives OUTSIDE #plan-main-content, so htmx grid swaps left the week label,
/// prev/next button URLs, This-week button visibility, Auto-fill state, and budget chip stale.
/// plantry-khw fixes this by re-emitting the plan-bar projections OOB on every response that
/// targets #plan-main-content.
/// plantry-pg6: budget chip id renamed from plan-bar-cost to plan-cost-chip for a stable id.
///
/// Acceptance criteria:
///   1. GET Grid carries plan-bar-nav, plan-cost-chip, and plan-bar-autofill projections
///      (OobContract — the three dynamic plan-bar elements that go stale after navigation).
///   2. GET Grid with a specific week reflects that week in the OOB nav (week label, nav URLs).
///   3. GET Grid for next week carries a plan-bar-nav that does NOT contain a "This week"
///      button when the current week is loaded, and DOES contain it when navigating away.
/// </summary>
[Collection(nameof(PlanBarNavOobCollection))]
public sealed class PlanBarNavOobContractTests(PlanBarNavOobFactory factory)
{
    private HttpClient CreateClient()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, WeekGridFixture.HouseholdId.ToString());
        return client;
    }

    // ── 1. Grid handler carries all plan-bar projections ──────────────────────

    [Fact(DisplayName = "GET Grid re-emits plan-bar-nav, plan-cost-chip, plan-bar-autofill OOB (OobContract — plantry-khw/plantry-pg6)")]
    public async Task GetGrid_CarriesPlanBarNavProjections()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/MealPlan?handler=Grid");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // ADR-013 OOB-contract (plantry-khw/plantry-pg6): the grid response must carry all three
        // plan-bar projections so the command bar stays coherent after htmx week navigation.
        // plan-cost-chip is the stable id for the budget chip (renamed from plan-bar-cost in plantry-pg6).
        OobContract.AssertCarriesProjections(html, "plan-bar-nav", "plan-cost-chip", "plan-bar-autofill");
    }

    // ── 2. Grid handler for a specific week reflects that week ────────────────

    [Fact(DisplayName = "GET Grid for next week carries plan-bar-nav with next week's label")]
    public async Task GetGrid_ForNextWeek_CarriesNextWeekLabel()
    {
        var client = CreateClient();

        // Navigate to a known future week so the week label is predictable.
        var targetMonday = new DateOnly(2026, 6, 22); // a known Monday
        var response = await client.GetAsync($"/MealPlan?handler=Grid&week={targetMonday:yyyy-MM-dd}");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // The plan-bar-nav OOB fragment must reflect the navigated week label.
        // "Jun 22" is the start of the week 2026-06-22 → 2026-06-28
        Assert.Contains("Jun 22", html);

        // Must carry all three plan-bar projections.
        OobContract.AssertCarriesProjections(html, "plan-bar-nav", "plan-cost-chip", "plan-bar-autofill");
    }

    // ── 3. This-week button: present when off this week, absent when on this week ──

    [Fact(DisplayName = "GET Grid for a different week's plan-bar-nav contains the 'This week' button")]
    public async Task GetGrid_ForDifferentWeek_PlanBarNav_ContainsThisWeekButton()
    {
        var client = CreateClient();

        // Use a far-future week that is guaranteed to differ from the real current week.
        // The This-week button appears when WeekStart != ThisWeekStart.
        var farFuture = new DateOnly(2030, 1, 7); // a known Monday well in the future
        var response = await client.GetAsync($"/MealPlan?handler=Grid&week={farFuture:yyyy-MM-dd}");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // The OOB plan-bar-nav fragment must include the "This week" button when navigated away.
        Assert.Contains("This week", html);
        Assert.Contains("wk-today", html);
    }

    // ── 4. Grid carries the wkgrid (primary swap target) AND the OOB projections ──

    [Fact(DisplayName = "GET Grid carries both wkgrid (primary swap) and plan-bar-nav OOB projections")]
    public async Task GetGrid_CarriesBothGridAndPlanBarNav()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/MealPlan?handler=Grid");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Primary swap target: the week grid
        Assert.Contains("wkgrid", html);
        // OOB projections: plan-bar updates
        Assert.Contains("plan-bar-nav", html);
        Assert.Contains("hx-swap-oob=\"true\"", html);
    }
}

[CollectionDefinition(nameof(PlanBarNavOobCollection))]
public sealed class PlanBarNavOobCollection : ICollectionFixture<PlanBarNavOobFactory> { }

/// <summary>WAF factory for plan-bar OOB contract tests. Uses the same stubs as WeekGridFragmentFactory.</summary>
public sealed class PlanBarNavOobFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName, _ => { });

            services.RemoveAll<IMealPlanRepository>();
            services.AddScoped<IMealPlanRepository>(_ => new FakeMealPlanRepo());

            services.RemoveAll<IMealSlotConfigRepository>();
            services.AddScoped<IMealSlotConfigRepository>(_ => new FakeSlotRepo(WeekGridFixture.SharedConfig));

            services.RemoveAll<IHouseholdMemberReader>();
            services.AddSingleton<IHouseholdMemberReader>(new FakeMemberReader(WeekGridFixture.Members));

            services.RemoveAll<IRecipeReadModel>();
            services.AddSingleton<IRecipeReadModel>(new FakeRecipeReader(WeekGridFixture.Recipes));

            services.RemoveAll<IMealPlanCatalogProductReader>();
            services.AddSingleton<IMealPlanCatalogProductReader>(new FakeProductReader(WeekGridFixture.Products));

            services.RemoveAll<AssignMealService>();
            services.AddScoped<AssignMealService>();
            services.RemoveAll<MoveMealService>();
            services.AddScoped<MoveMealService>();

            services.RemoveAll<IMealPlanStockReader>();
            services.AddSingleton<IMealPlanStockReader>(new NullStockReader());
            services.RemoveAll<IMealPlanPriceReader>();
            services.AddSingleton<IMealPlanPriceReader>(new NullPriceReader());
            services.RemoveAll<IMealPlanShoppingWriter>();
            services.AddSingleton<IMealPlanShoppingWriter>(new NullShoppingWriter());

            // ADR-021 week read model: return empty bag — no DB connection in WAF tests.
            services.RemoveAll<IMealPlanWeekReadModel>();
            services.AddSingleton<IMealPlanWeekReadModel>(new NullWeekReadModel());

            services.RemoveAll<PlanFulfillmentService>();
            services.AddScoped<PlanFulfillmentService>();
            services.RemoveAll<PlanCostingService>();
            services.AddScoped<PlanCostingService>();
            services.RemoveAll<ShopForWeekService>();
            services.AddScoped<ShopForWeekService>();

            services.RemoveAll<IMealPlanner>();
            services.AddSingleton<IMealPlanner>(new NullMealPlanner());
            services.RemoveAll<IPendingProposalStore>();
            services.AddSingleton<IPendingProposalStore>(new NullPendingProposalStore());
            services.RemoveAll<GeneratePlanService>();
            services.AddScoped<GeneratePlanService>();
            services.RemoveAll<AcceptProposalService>();
            services.AddScoped<AcceptProposalService>();

            services.RemoveAll<IUserPreferenceRepository>();
            services.AddSingleton<IUserPreferenceRepository>(new NullPrefsRepo());

            // so5.5: stub ITagReader (needed by GeneratePlanService for unfulfillable tag name resolution)
            services.RemoveAll<ITagReader>();
            services.AddSingleton<ITagReader>(new NullTagReader());

            services.RemoveAll<IMealPlanExpiringStockReader>();
            services.AddSingleton<IMealPlanExpiringStockReader>(new NullExpiringStockReader());
            services.RemoveAll<PlanInsightsService>();
            services.AddScoped<PlanInsightsService>();

            // plantry-so5.3: stub planning settings repos
            services.RemoveAll<IHouseholdPlanningSettingsRepository>();
            services.AddSingleton<IHouseholdPlanningSettingsRepository>(new NullPlanningSettingsRepo());
            services.RemoveAll<IWeekPlanningOverrideRepository>();
            services.AddSingleton<IWeekPlanningOverrideRepository>(new NullWeekOverrideRepo());
            services.RemoveAll<SetPlanningSettingsService>();
            services.AddScoped<SetPlanningSettingsService>();
        });
    }
}

// NullTagReader is defined in ConflictCellFragmentTests.cs (shared across the MealPlanning test namespace).
