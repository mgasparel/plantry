using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Identity.Infrastructure;
using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Plantry.SharedKernel.Domain;
using Plantry.Web.MealPlanning;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// Shared base WAF factory for the MealPlanning L4 fragment-test suite (plantry-sl2e). Promoted and
/// renamed from the former <c>WeekGridFragmentFactory</c> (previously inline at the bottom of
/// WeekGridFragmentTests.cs) — converging on the house pattern the other 12 shared
/// <c>Infrastructure/</c> factories already use (e.g. <see cref="RecipeDetailFragmentFactory"/>,
/// <see cref="ShoppingListFragmentFactory"/>): a non-sealed base with <c>protected virtual</c> hooks
/// that a derived factory overrides only for the registrations its scenario actually varies.
///
/// Wires the full MealPlanning fake-service graph for all 21 MealPlanning WAF factories converged
/// in plantry-sl2e (the ticket's enumeration of 18 undercounted by 3 — MealCardProductPhotoFactory,
/// ProductBatchingFactory, RecipeOnlyBatchingFactory): household display currency, expiring-soon
/// horizon, header-driven test auth, the UserManager stub, the meal-plan/slot-config/member/recipe/
/// catalog fakes, the P3-4 stock/price/
/// shopping/cook-status ports, the P3-6a planner/proposal-store pair, planning-settings repos, and a
/// pinned <see cref="IClock"/> (plantry-1w87), unconditionally, so the SUT and a test's fixture
/// never race two independent reads of the real system clock.
///
/// A hook is evaluated exactly once per <see cref="ConfigureWebHost"/> call (the values are handed
/// straight to <c>AddSingleton</c>, not re-evaluated per request) so a derived factory that backs a
/// hook with a field (e.g. <c>public XRepo Repo { get; } = new();</c>, overriding
/// <see cref="MealPlanRepo"/> to return it) gets the single shared, stateful instance its scenario
/// needs — the same instance every request sees, exactly like every pre-promotion factory that
/// captured a repo field in its <c>ConfigureTestServices</c> closure.
/// </summary>
public class MealPlanFragmentFactory : WebApplicationFactory<Program>
{
    /// <summary>Override to true to return no slots from the fake repo (tests empty state).</summary>
    protected virtual bool NoSlots => false;

    /// <summary>
    /// Whether to stub <see cref="UserManager{TUser}"/> off the real Identity database. False only
    /// for a factory whose handlers never resolve the current user — GET-only, no Assign/Generate/Eat
    /// POST (<c>PlanBarNavOobFactory</c>).
    /// </summary>
    protected virtual bool StubUserManager => true;

    /// <summary>The fixed user id <see cref="FakeUserManager"/> resolves as "current user" — varies
    /// per factory only because each was independently authored; the value itself is never asserted
    /// on, so any stable id works.</summary>
    protected virtual string FakeUserId => "00000000-0000-0000-0000-0000000000aa";

    /// <summary>The household's display currency (plantry-2x6e.1).</summary>
    protected virtual string DisplayCurrency => "USD";

    protected virtual IMealPlanRepository MealPlanRepo => new FakeMealPlanRepo();

    protected virtual IMealSlotConfigRepository SlotConfigRepo =>
        new FakeSlotRepo(NoSlots ? null : WeekGridFixture.SharedConfig);

    protected virtual IHouseholdMemberReader MemberReader => new FakeMemberReader(WeekGridFixture.Members);

    protected virtual IRecipeReadModel RecipeReadModel => new FakeRecipeReader(WeekGridFixture.Recipes);

    protected virtual IMealPlanCatalogProductReader CatalogProductReader =>
        new FakeProductReader(WeekGridFixture.Products);

    /// <summary>ADR-021 week read model — an empty bag by default; no DB connection in WAF tests.</summary>
    protected virtual IMealPlanWeekReadModel WeekReadModel => new NullWeekReadModel();

    protected virtual IUserPreferenceRepository PreferenceRepo => new NullPrefsRepo();

    /// <summary>Needed by <see cref="GeneratePlanService"/> for unfulfillable-tag name resolution.</summary>
    protected virtual ITagReader TagReader => new NullTagReader();

    /// <summary>P3-4 port — no product dishes tracked by default.</summary>
    protected virtual IMealPlanStockReader StockReader => new NullStockReader();

    protected virtual IMealPlanCookStatusReader CookStatusReader => new NullCookStatusReader();

    /// <summary>
    /// Non-null only for a factory whose scenario drives the real Eat/UndoEat write port (typically a
    /// spy doubling as <see cref="IMealPlanCookStatusReader"/> so a POST's effect is visible on the
    /// very next cell re-render). Left unregistered by default, same as every pre-promotion factory
    /// that never touched this port.
    /// </summary>
    protected virtual IMealPlanEatWriter? EatWriter => null;

    /// <summary>P3-6a AI planner.</summary>
    protected virtual IMealPlanner Planner => new NullMealPlanner();

    /// <summary>P3-6a pending-proposal store.</summary>
    protected virtual IPendingProposalStore ProposalStore => new NullPendingProposalStore();

    /// <summary>plantry-so5.3 household-default planning settings.</summary>
    protected virtual IHouseholdPlanningSettingsRepository PlanningSettingsRepo => new NullPlanningSettingsRepo();

    /// <summary>plantry-so5.3 per-week planning-settings override.</summary>
    protected virtual IWeekPlanningOverrideRepository WeekOverrideRepo => new NullWeekOverrideRepo();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // Household display currency: budget writes stamp it and cost figures render through
            // MoneyDisplay with it; stub so the DB-backed service isn't hit.
            services.AddFakeDisplayCurrency(DisplayCurrency);
            services.AddFakeExpiringSoonHorizon();

            // Auth: header-driven test scheme.
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            if (StubUserManager)
            {
                services.RemoveAll<UserManager<AppUser>>();
                services.AddSingleton<UserManager<AppUser>>(new FakeUserManager(new AppUser { Id = FakeUserId }));
            }

            // Replace MealPlan repository.
            services.RemoveAll<IMealPlanRepository>();
            services.AddSingleton(MealPlanRepo);

            // Replace slot config repo.
            services.RemoveAll<IMealSlotConfigRepository>();
            services.AddSingleton(SlotConfigRepo);

            // Replace household member reader.
            services.RemoveAll<IHouseholdMemberReader>();
            services.AddSingleton(MemberReader);

            // Replace recipe and catalog readers.
            services.RemoveAll<IRecipeReadModel>();
            services.AddSingleton(RecipeReadModel);
            services.RemoveAll<IMealPlanCatalogProductReader>();
            services.AddSingleton(CatalogProductReader);

            // Re-register services that depend on the fakes.
            services.RemoveAll<AssignMealService>();
            services.AddScoped<AssignMealService>();
            services.RemoveAll<MoveMealService>();
            services.AddScoped<MoveMealService>();

            // Stub the P3-4 port interfaces so PlanFulfillmentService / PlanCostingService /
            // ShopForWeekService resolve without real Inventory/Pricing/Shopping infrastructure.
            services.RemoveAll<IMealPlanStockReader>();
            services.AddSingleton(StockReader);
            services.RemoveAll<IMealPlanPriceReader>();
            services.AddSingleton<IMealPlanPriceReader>(new NullPriceReader());
            // ShopForWeekService calls Shopping's AddItemCommand directly (intra-context since the
            // Planning merge, ADR-024) — stub its two dependencies instead of the former
            // IMealPlanShoppingWriter port.
            services.RemoveAll<IShoppingListRepository>();
            services.AddSingleton<IShoppingListRepository>(new NullShoppingListRepository());
            services.RemoveAll<IShoppingCatalogReader>();
            services.AddSingleton<IShoppingCatalogReader>(new NullShoppingCatalogReader());

            if (EatWriter is { } eatWriter)
            {
                services.RemoveAll<IMealPlanEatWriter>();
                services.AddSingleton(eatWriter);
            }
            services.RemoveAll<IMealPlanCookStatusReader>();
            services.AddSingleton(CookStatusReader);

            services.RemoveAll<IMealPlanWeekReadModel>();
            services.AddSingleton(WeekReadModel);

            services.RemoveAll<PlanFulfillmentService>();
            services.AddScoped<PlanFulfillmentService>();
            services.RemoveAll<PlanCostingService>();
            services.AddScoped<PlanCostingService>();
            services.RemoveAll<ShopForWeekService>();
            services.AddScoped<ShopForWeekService>();

            // P3-6a: AI planner, proposal store, and application services.
            services.RemoveAll<IMealPlanner>();
            services.AddSingleton(Planner);
            services.RemoveAll<IPendingProposalStore>();
            services.AddSingleton(ProposalStore);
            services.RemoveAll<GeneratePlanService>();
            services.AddScoped<GeneratePlanService>();
            services.RemoveAll<AcceptProposalService>();
            services.AddScoped<AcceptProposalService>();

            services.RemoveAll<IUserPreferenceRepository>();
            services.AddSingleton(PreferenceRepo);

            services.RemoveAll<ITagReader>();
            services.AddSingleton(TagReader);

            // P3-5: expiring-stock reader; re-register insights service.
            services.RemoveAll<IMealPlanExpiringStockReader>();
            services.AddSingleton<IMealPlanExpiringStockReader>(new NullExpiringStockReader());
            services.RemoveAll<PlanInsightsService>();
            services.AddScoped<PlanInsightsService>();

            // plantry-so5.3: planning settings repos.
            services.RemoveAll<IHouseholdPlanningSettingsRepository>();
            services.AddSingleton(PlanningSettingsRepo);
            services.RemoveAll<IWeekPlanningOverrideRepository>();
            services.AddSingleton(WeekOverrideRepo);
            services.RemoveAll<SetPlanningSettingsService>();
            services.AddScoped<SetPlanningSettingsService>();

            // Pin IClock so the SUT never races an independent live-clock read (plantry-1w87).
            services.RemoveAll<IClock>();
            services.AddScoped<IClock>(_ => new FixedClock(MealPlanningTestClock.Instant));
        });
    }
}
