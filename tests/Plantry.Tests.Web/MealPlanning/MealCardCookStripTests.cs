using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Identity.Infrastructure;
using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;
using Plantry.Tests.Web.MealPlanning;
using Plantry.Tests.Web.Preferences;
using Plantry.Web.MealPlanning;
using Xunit;
using SharedSystemClock = Plantry.SharedKernel.Domain.SystemClock;

namespace Plantry.Tests.Web.MealPlanning;

/// <summary>
/// L4 fragment tests for the plan card Cook strip (plantry-0eut): pending recipe-dish Cook links,
/// done rows, the fully-cooked pill/card treatment, and the today/past-vs-future/note gating. The
/// real <c>IMealPlanCookStatusReader</c> composition adapter is swapped for a fixed fake here (this
/// suite owns the strip's RENDERING contract, not the adapter's derivation logic — that is covered by
/// <c>MealPlanCookStatusReaderAdapterTests</c> at L2 and by the EF read-side integration tests).
/// </summary>
public sealed class MealCardCookStripTests
{
    [Fact(DisplayName = "GET /MealPlan: partially-cooked meal shows a done row and a pending Cook link")]
    public async Task Partially_Cooked_Meal_Shows_Done_Row_And_Cook_Link()
    {
        await using var factory = new MealCardCookStripFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, CookStripFixture.HouseholdId.ToString());

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Breakfast: pending dish (servings=2, multi-dish meal → disambiguated "Cook <name>" label)
        // renders a live Cook deep-link carrying id/servings/plannedDishId.
        Assert.Contains("mc-cook-act", html);
        Assert.Contains("Cook Unknown recipe", html);
        Assert.Contains($"/Recipes/Cook?id={factory.Repo.PendingRecipeRecipeId:D}", html);
        Assert.Contains("servings=2", html);
        Assert.Contains($"plannedDishId={factory.Repo.PendingRecipeDishId:D}", html);
        // plantry-iejb's leftover-prefill seam: eatingTonight = the meal's AttendeesOverride count,
        // carried onto the plan-launched Cook link (Cook.cshtml.cs EatingTonight doc comment).
        Assert.Contains($"eatingTonight={factory.Repo.EatingTonightForBreakfast}", html);

        // Breakfast's other dish (servings=3) is already done — a settled row, not a button.
        Assert.Contains("mc-cook-done", html);
        Assert.Contains("Cooked · 3 srv", html);
    }

    [Fact(DisplayName = "GET /MealPlan: a meal whose every dish is done gets the cooked pill + card treatment")]
    public async Task Fully_Cooked_Meal_Shows_Pill_And_Cooked_Card_Class()
    {
        await using var factory = new MealCardCookStripFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, CookStripFixture.HouseholdId.ToString());

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Lunch: single dish (servings=1), done — the ONLY fully-cooked meal on the page.
        Assert.Contains("Cooked · 1 srv", html);
        Assert.Contains("mc-cooked", html); // the corner pill
        Assert.Contains("meal-card  cooked\"", html); // isNote="" + allDishesDone="cooked" → double space
    }

    [Fact(DisplayName = "GET /MealPlan: a note meal never renders a Cook strip, even when dated today")]
    public async Task Note_Meal_Renders_No_Cook_Strip()
    {
        await using var factory = new MealCardCookStripFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, CookStripFixture.HouseholdId.ToString());

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Free note · no dishes", html); // the Dinner note card rendered at all
        // Exactly two strips on the page — Breakfast (partial) and Lunch (fully cooked). The Dinner
        // note card, despite being dated the same as both, contributes a third only if the note
        // branch leaked strip markup — it must not.
        var stripCount = html.Split("mc-cook-strip").Length - 1;
        Assert.Equal(2, stripCount);
    }

    [Fact(DisplayName = "GET /MealPlan?week=<future>: a future dish-based meal renders no Cook strip")]
    public async Task Future_Meal_Renders_No_Cook_Strip()
    {
        await using var factory = new MealCardCookStripFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, CookStripFixture.HouseholdId.ToString());

        var response = await client.GetAsync($"/MealPlan?week={factory.Repo.FutureWeekMonday:yyyy-MM-dd}");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Sanity: the future week's card rendered at all (its one dish, name-resolved to "Unknown
        // recipe" by the empty week read model — same fallback used on the "this week" response).
        Assert.Contains("Unknown recipe", html);
        Assert.DoesNotContain("mc-cook-strip", html);
        Assert.DoesNotContain("mc-cook-act", html);
        Assert.DoesNotContain("mc-cook-done", html);
    }
}

/// <summary>WAF factory wiring a fixed cook-status fake and a two-week meal plan fixture (plantry-0eut).</summary>
public sealed class MealCardCookStripFactory : WebApplicationFactory<Program>
{
    public CookStripMealPlanRepo Repo { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.AddFakeDisplayCurrency("USD");
            services.AddFakeExpiringSoonHorizon();
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.RemoveAll<UserManager<AppUser>>();
            services.AddSingleton<UserManager<AppUser>>(
                new FakeUserManager(new AppUser { Id = "00000000-0000-0000-0000-0000000000cc" }));

            services.RemoveAll<IMealPlanRepository>();
            services.AddSingleton<IMealPlanRepository>(Repo);

            services.RemoveAll<IMealSlotConfigRepository>();
            services.AddScoped<IMealSlotConfigRepository>(_ => new FakeSlotRepo(CookStripFixture.SlotConfig));

            services.RemoveAll<IHouseholdMemberReader>();
            services.AddSingleton<IHouseholdMemberReader>(new FakeMemberReader([]));

            services.RemoveAll<IRecipeReadModel>();
            services.AddSingleton<IRecipeReadModel>(new FakeRecipeReader([]));

            services.RemoveAll<IMealPlanWeekReadModel>();
            services.AddSingleton<IMealPlanWeekReadModel>(new NullWeekReadModel());

            services.RemoveAll<IMealPlanCatalogProductReader>();
            services.AddSingleton<IMealPlanCatalogProductReader>(new FakeCatalogProductReaderW(existsResult: true));

            // The port under test's rendering contract — fixed statuses keyed by the plan's REAL
            // (repo-generated) PlannedDish ids, captured by CookStripMealPlanRepo at construction.
            services.RemoveAll<IMealPlanCookStatusReader>();
            services.AddSingleton<IMealPlanCookStatusReader>(new FixedCookStatusReader(
                new Dictionary<Guid, DishCookStatus>
                {
                    [Repo.DoneRecipeDishIdA] = new DishCookStatus(DateTimeOffset.UtcNow.AddMinutes(-30)),
                    [Repo.DoneRecipeDishIdB] = new DishCookStatus(DateTimeOffset.UtcNow.AddMinutes(-10)),
                }));

            services.RemoveAll<IMealPlanStockReader>();
            services.AddSingleton<IMealPlanStockReader>(new NullStockReader());
            services.RemoveAll<IMealPlanPriceReader>();
            services.AddSingleton<IMealPlanPriceReader>(new NullPriceReader());
            services.RemoveAll<IMealPlanShoppingWriter>();
            services.AddSingleton<IMealPlanShoppingWriter>(new NullShoppingWriter());

            services.RemoveAll<PlanFulfillmentService>();
            services.AddScoped<PlanFulfillmentService>();
            services.RemoveAll<PlanCostingService>();
            services.AddScoped<PlanCostingService>();
            services.RemoveAll<ShopForWeekService>();
            services.AddScoped<ShopForWeekService>();

            services.RemoveAll<AssignMealService>();
            services.AddScoped<AssignMealService>();
            services.RemoveAll<MoveMealService>();
            services.AddScoped<MoveMealService>();

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

            services.RemoveAll<ITagReader>();
            services.AddSingleton<ITagReader>(new NullTagReader());

            services.RemoveAll<IMealPlanExpiringStockReader>();
            services.AddSingleton<IMealPlanExpiringStockReader>(new NullExpiringStockReader());
            services.RemoveAll<PlanInsightsService>();
            services.AddScoped<PlanInsightsService>();

            services.RemoveAll<IHouseholdPlanningSettingsRepository>();
            services.AddSingleton<IHouseholdPlanningSettingsRepository>(new NullPlanningSettingsRepo());
            services.RemoveAll<IWeekPlanningOverrideRepository>();
            services.AddSingleton<IWeekPlanningOverrideRepository>(new NullWeekOverrideRepo());
            services.RemoveAll<SetPlanningSettingsService>();
            services.AddScoped<SetPlanningSettingsService>();
        });
    }
}

// ── Cook-strip test doubles ────────────────────────────────────────────────────

/// <summary>Shared stable slot identifiers for the cook-strip test scenario.</summary>
internal static class CookStripFixture
{
    public static readonly Guid HouseholdId = Guid.Parse("55555555-0000-0000-0000-000000000005");

    private static readonly HouseholdId HhId = SharedKernel.HouseholdId.From(HouseholdId);
    public static readonly MealSlotConfig SlotConfig = MealSlotConfig.CreateWithDefaults(HhId, SharedSystemClock.Instance);

    private static readonly List<MealSlot> OrderedSlots = [.. SlotConfig.Slots.OrderBy(s => s.Ordinal)];
    public static readonly MealSlotId BreakfastSlotId = OrderedSlots[0].Id;
    public static readonly MealSlotId LunchSlotId = OrderedSlots[1].Id;
    public static readonly MealSlotId DinnerSlotId = OrderedSlots[2].Id;
}

/// <summary>
/// Meal plan repo backing the cook-strip scenario: TWO weeks (this week + 60 days out), so the
/// gating tests can request each independently via <c>?week=</c>. This week carries a partially-
/// cooked meal (Breakfast), a fully-cooked meal (Lunch), and a note meal (Dinner) — all dated the
/// week's Monday, which is always <c>&lt;= today</c>. The future week carries one pending dish-based
/// meal, dated 60 days out so it is unambiguously future regardless of which day of the week "today"
/// happens to be when the suite runs.
/// </summary>
public sealed class CookStripMealPlanRepo : IMealPlanRepository
{
    public MealPlan ThisWeekPlan { get; }
    public MealPlan FutureWeekPlan { get; }
    public DateOnly ThisWeekMonday { get; }
    public DateOnly FutureWeekMonday { get; }

    /// <summary>The recipe id backing the Breakfast card's still-pending dish — used to assert the Cook link's <c>id=</c>.</summary>
    public Guid PendingRecipeRecipeId { get; } = Guid.CreateVersion7();

    public Guid PendingRecipeDishId { get; private set; }
    public Guid DoneRecipeDishIdA { get; private set; }
    public Guid DoneRecipeDishIdB { get; private set; }
    public Guid FutureRecipeDishId { get; private set; }

    /// <summary>Expected <c>eatingTonight</c> value on the Breakfast Cook link — the meal's AttendeesOverride count.</summary>
    public int EatingTonightForBreakfast { get; private set; }

    public CookStripMealPlanRepo()
    {
        var hhId = SharedKernel.HouseholdId.From(CookStripFixture.HouseholdId);
        var clock = SharedSystemClock.Instance;
        var today = DateOnly.FromDateTime(DateTime.Today);
        ThisWeekMonday = MealPlan.NormalizeToMonday(today);
        FutureWeekMonday = MealPlan.NormalizeToMonday(today.AddDays(60));

        var recipeDoneA = Guid.CreateVersion7();
        var recipeDoneB = Guid.CreateVersion7();
        var recipeFuture = Guid.CreateVersion7();

        // Two attendees for the Breakfast meal — proves the Cook link's eatingTonight param carries
        // the REAL AttendeesOverride count (plantry-iejb's leftover-prefill seam) rather than a
        // coincidental zero default.
        var breakfastAttendees = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        EatingTonightForBreakfast = breakfastAttendees.Count;

        ThisWeekPlan = MealPlan.Start(hhId, ThisWeekMonday, clock);
        // Breakfast: partially cooked — one pending dish, one already-done dish.
        ThisWeekPlan.AssignMeal(ThisWeekMonday, CookStripFixture.BreakfastSlotId,
            [
                new DishSpec(DishKind.Recipe, PendingRecipeRecipeId, 2),
                new DishSpec(DishKind.Recipe, recipeDoneA, 3),
            ],
            breakfastAttendees, "manual", Guid.Empty, clock);
        // Lunch: fully cooked — single done dish.
        ThisWeekPlan.AssignMeal(ThisWeekMonday, CookStripFixture.LunchSlotId,
            [new DishSpec(DishKind.Recipe, recipeDoneB, 1)],
            null, "manual", Guid.Empty, clock);
        // Dinner: note meal — must never render a strip, regardless of date.
        ThisWeekPlan.AssignNote(ThisWeekMonday, CookStripFixture.DinnerSlotId, "Takeout night", null, "manual", Guid.Empty, clock);

        FutureWeekPlan = MealPlan.Start(hhId, FutureWeekMonday, clock);
        FutureWeekPlan.AssignMeal(FutureWeekMonday, CookStripFixture.BreakfastSlotId,
            [new DishSpec(DishKind.Recipe, recipeFuture, 4)],
            null, "manual", Guid.Empty, clock);

        // Capture the REAL repo-generated PlannedDish ids so the fixed cook-status fake and the
        // test assertions can key off them.
        var breakfast = ThisWeekPlan.PlannedMeals.Single(m => m.MealSlotId == CookStripFixture.BreakfastSlotId);
        PendingRecipeDishId = breakfast.PlannedDishes.Single(d => d.RecipeId == PendingRecipeRecipeId).Id.Value;
        DoneRecipeDishIdA = breakfast.PlannedDishes.Single(d => d.RecipeId == recipeDoneA).Id.Value;

        var lunch = ThisWeekPlan.PlannedMeals.Single(m => m.MealSlotId == CookStripFixture.LunchSlotId);
        DoneRecipeDishIdB = lunch.PlannedDishes.Single(d => d.RecipeId == recipeDoneB).Id.Value;

        var future = FutureWeekPlan.PlannedMeals.Single(m => m.MealSlotId == CookStripFixture.BreakfastSlotId);
        FutureRecipeDishId = future.PlannedDishes.Single(d => d.RecipeId == recipeFuture).Id.Value;
    }

    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
    {
        if (weekStart == ThisWeekMonday) return Task.FromResult<MealPlan?>(ThisWeekPlan);
        if (weekStart == FutureWeekMonday) return Task.FromResult<MealPlan?>(FutureWeekPlan);
        return Task.FromResult<MealPlan?>(null);
    }

    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default) =>
        Task.FromResult(weekStart == FutureWeekMonday ? FutureWeekPlan : ThisWeekPlan);

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Fixed <see cref="IMealPlanCookStatusReader"/> — returns exactly the pre-seeded statuses, filtered to what was asked for.</summary>
internal sealed class FixedCookStatusReader(IReadOnlyDictionary<Guid, DishCookStatus> statuses) : IMealPlanCookStatusReader
{
    public Task<IReadOnlyDictionary<Guid, DishCookStatus>> GetStatusesAsync(
        IReadOnlyCollection<Guid> plannedDishIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, DishCookStatus> result = plannedDishIds
            .Where(statuses.ContainsKey)
            .ToDictionary(id => id, id => statuses[id]);
        return Task.FromResult(result);
    }
}
