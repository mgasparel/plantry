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
using Plantry.Tests.Web.Preferences;
using Plantry.Web.MealPlanning;
using System.Text.Json;
using Xunit;

namespace Plantry.Tests.Web.MealPlanning;

/// <summary>
/// L4 regression coverage for plantry-ri26: a product dish planned onto a meal was always labelled
/// with the hardcoded word "servings" (editor stepper, dish search) or rendered with NO unit at all
/// (Cook strip's pending "Eat" button and done "Eaten ·" row) — regardless of the product's actually
/// configured default unit. The fix threads a real unit CODE (e.g. "ea") through every one of those
/// surfaces. Covers the three server-rendered/hydrated hops:
///   1. The Cook strip's pending "Eat" button and done "Eaten ·" row (_MealCard.cshtml).
///   2. GET ?handler=EditorJson's dishes[].unitCode — the edit-existing-meal hydration hop.
///   3. GET ?handler=SearchJson's product hits — the dish-search hop.
/// A same-meal recipe dish acts as a control in the Cook-strip tests: it must keep rendering the
/// unrelated "servings" (srv) label untouched, proving the fix is additive for products only.
/// </summary>
public sealed class MealPlanProductUnitLabelTests
{
    private static HttpClient CreateClient(ProductUnitLabelFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ProductUnitLabelFixture.HouseholdId.ToString());
        return client;
    }

    [Fact(DisplayName = "GET /MealPlan: pending product dish's Eat button shows the product's configured unit, not a bare number")]
    public async Task PendingProductDish_EatButton_ShowsConfiguredUnit()
    {
        await using var factory = new ProductUnitLabelFactory();
        var client = CreateClient(factory);

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Lunch's pending product dish (5 units, unresolved cook status) renders the Eat button with
        // the product's real unit code "ea" — not a bare "5" (the pre-fix bug: no unit at all).
        Assert.Contains("act-srv\">5 ea</span>", html);
        Assert.DoesNotContain("act-srv\">5</span>", html);
    }

    [Fact(DisplayName = "GET /MealPlan: a done product dish's Cook-strip row shows the product's configured unit")]
    public async Task DoneProductDish_ShowsConfiguredUnit()
    {
        await using var factory = new ProductUnitLabelFactory();
        var client = CreateClient(factory);

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Breakfast's already-eaten product dish (3 units) shows "Eaten · 3 ea" — not the pre-fix
        // bare "Eaten · 3" with no unit at all.
        Assert.Contains("Eaten · 3 ea", html);
        Assert.DoesNotContain("Eaten · 3<", html);

        // Control: the recipe dish sharing the same meal keeps its unrelated "servings" label
        // untouched — the fix is additive for products only.
        Assert.Contains("Cooked · 2 srv", html);
    }

    [Fact(DisplayName = "GET ?handler=EditorJson for an existing product-dish meal includes the product's unit code")]
    public async Task EditorJson_ExistingProductDish_IncludesUnitCode()
    {
        await using var factory = new ProductUnitLabelFactory();
        var client = CreateClient(factory);

        var response = await client.GetAsync(
            $"/MealPlan?handler=EditorJson&date={factory.Repo.ThisWeekMonday:yyyy-MM-dd}" +
            $"&slotId={ProductUnitLabelFixture.LunchSlotId.Value:D}&mealId={factory.Repo.LunchMealId:D}");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);

        var dishes = doc.RootElement.GetProperty("dishes");
        Assert.Equal(1, dishes.GetArrayLength());
        var dish = dishes[0];
        Assert.Equal("product", dish.GetProperty("kind").GetString());
        Assert.True(dish.TryGetProperty("unitCode", out var unitCodeEl), "dishes[0] missing unitCode");
        Assert.Equal("ea", unitCodeEl.GetString());
    }

    [Fact(DisplayName = "GET ?handler=SearchJson product hits carry the product's unit code")]
    public async Task SearchJson_ProductHit_IncludesUnitCode()
    {
        await using var factory = new ProductUnitLabelFactory();
        var client = CreateClient(factory);

        var response = await client.GetAsync("/MealPlan?handler=SearchJson&q=Flour");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);

        var hits = doc.RootElement.GetProperty("hits");
        Assert.True(hits.GetArrayLength() > 0, "Expected at least one product search hit for 'Flour'.");
        var hit = hits[0];
        Assert.Equal("product", hit.GetProperty("kind").GetString());
        Assert.True(hit.TryGetProperty("unitCode", out var unitCodeEl), "product hit missing unitCode");
        Assert.Equal("ea", unitCodeEl.GetString());
    }
}

// ── Fixture ───────────────────────────────────────────────────────────────────

internal static class ProductUnitLabelFixture
{
    public static readonly Guid HouseholdId = Guid.Parse("77777777-0000-0000-0000-000000000007");

    private static readonly HouseholdId HhId = SharedKernel.HouseholdId.From(HouseholdId);
    public static readonly MealSlotConfig SlotConfig =
        MealSlotConfig.CreateWithDefaults(HhId, new FixedClock(MealPlanningTestClock.Instant));

    private static readonly List<MealSlot> OrderedSlots = [.. SlotConfig.Slots.OrderBy(s => s.Ordinal)];
    public static readonly MealSlotId BreakfastSlotId = OrderedSlots[0].Id;
    public static readonly MealSlotId LunchSlotId = OrderedSlots[1].Id;

    /// <summary>The one product used across every scenario in this file — its configured default
    /// unit code is always "ea" per <see cref="StubUnitCodeCatalogProductReader"/>.</summary>
    public static readonly Guid FlourProductId = Guid.CreateVersion7();
}

// ── Factory ───────────────────────────────────────────────────────────────────

/// <summary>
/// WAF factory wiring a plan with a done+pending product dish (plus a control recipe dish) and a
/// catalog reader that resolves the product's unit code to "ea". Mirrors
/// <c>MealCardCookStripFactory</c>'s service wiring (WeekGridFragmentTests.cs /
/// MealCardCookStripTests.cs) — this suite differs only in the plan repo and catalog reader.
/// </summary>
public sealed class ProductUnitLabelFactory : WebApplicationFactory<Program>
{
    public ProductUnitLabelMealPlanRepo Repo { get; } = new();

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
                new FakeUserManager(new AppUser { Id = "00000000-0000-0000-0000-0000000000dd" }));

            services.RemoveAll<IMealPlanRepository>();
            services.AddSingleton<IMealPlanRepository>(Repo);

            services.RemoveAll<IMealSlotConfigRepository>();
            services.AddScoped<IMealSlotConfigRepository>(_ => new FakeSlotRepo(ProductUnitLabelFixture.SlotConfig));

            services.RemoveAll<IHouseholdMemberReader>();
            services.AddSingleton<IHouseholdMemberReader>(new FakeMemberReader([]));

            services.RemoveAll<IRecipeReadModel>();
            services.AddSingleton<IRecipeReadModel>(new FakeRecipeReader([]));

            services.RemoveAll<IMealPlanWeekReadModel>();
            services.AddSingleton<IMealPlanWeekReadModel>(new NullWeekReadModel());

            // The port under test: resolves the one product's unit code to "ea" everywhere
            // (SearchAsync, ResolveDefaultUnitCodesAsync) — plantry-ri26.
            services.RemoveAll<IMealPlanCatalogProductReader>();
            services.AddSingleton<IMealPlanCatalogProductReader>(new StubUnitCodeCatalogProductReader());

            services.RemoveAll<IMealPlanCookStatusReader>();
            services.AddSingleton<IMealPlanCookStatusReader>(new FixedCookStatusReader(
                new Dictionary<Guid, DishCookStatus>
                {
                    [Repo.DoneRecipeDishId] = new DishCookStatus(MealPlanningTestClock.Instant.AddMinutes(-30)),
                    [Repo.DoneProductDishId] = new DishCookStatus(MealPlanningTestClock.Instant.AddMinutes(-20)),
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

            // Pin IClock to the same instant the fixture derives "today" from (plantry-1w87 pattern),
            // so the SUT and the fixture never race two independent reads of the real system clock.
            services.RemoveAll<IClock>();
            services.AddScoped<IClock>(_ => new FixedClock(MealPlanningTestClock.Instant));
        });
    }
}

// ── Plan repo ─────────────────────────────────────────────────────────────────

/// <summary>
/// Meal plan repo for the plantry-ri26 unit-label scenario: Breakfast carries a done recipe dish
/// (control) plus a done product dish; Lunch carries one still-pending product dish. All dated the
/// current week's Monday.
/// </summary>
public sealed class ProductUnitLabelMealPlanRepo : IMealPlanRepository
{
    private static readonly IClock _clock = new FixedClock(MealPlanningTestClock.Instant);

    public MealPlan ThisWeekPlan { get; }
    public DateOnly ThisWeekMonday { get; }

    public Guid PancakesRecipeId { get; } = Guid.CreateVersion7();
    public Guid DoneRecipeDishId { get; private set; }
    public Guid DoneProductDishId { get; private set; }
    public Guid PendingProductDishId { get; private set; }
    public Guid LunchMealId { get; private set; }

    public ProductUnitLabelMealPlanRepo()
    {
        var hhId = SharedKernel.HouseholdId.From(ProductUnitLabelFixture.HouseholdId);
        var today = DateOnly.FromDateTime(MealPlanningTestClock.Instant.UtcDateTime);
        ThisWeekMonday = MealPlan.NormalizeToMonday(today);

        ThisWeekPlan = MealPlan.Start(hhId, ThisWeekMonday, _clock);

        // Breakfast: a done recipe dish (control — must keep showing "srv") + a done product dish.
        ThisWeekPlan.AssignMeal(ThisWeekMonday, ProductUnitLabelFixture.BreakfastSlotId,
            [
                new DishSpec(DishKind.Recipe, PancakesRecipeId, 2),
                new DishSpec(DishKind.Product, ProductUnitLabelFixture.FlourProductId, 3),
            ],
            null, "manual", Guid.Empty, _clock);

        // Lunch: one still-pending product dish.
        ThisWeekPlan.AssignMeal(ThisWeekMonday, ProductUnitLabelFixture.LunchSlotId,
            [new DishSpec(DishKind.Product, ProductUnitLabelFixture.FlourProductId, 5)],
            null, "manual", Guid.Empty, _clock);

        var breakfast = ThisWeekPlan.PlannedMeals.Single(m => m.MealSlotId == ProductUnitLabelFixture.BreakfastSlotId);
        DoneRecipeDishId = breakfast.PlannedDishes.Single(d => d.RecipeId == PancakesRecipeId).Id.Value;
        DoneProductDishId = breakfast.PlannedDishes.Single(d => d.ProductId == ProductUnitLabelFixture.FlourProductId).Id.Value;

        var lunch = ThisWeekPlan.PlannedMeals.Single(m => m.MealSlotId == ProductUnitLabelFixture.LunchSlotId);
        PendingProductDishId = lunch.PlannedDishes.Single().Id.Value;
        LunchMealId = lunch.Id.Value;
    }

    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
        => Task.FromResult<MealPlan?>(weekStart == ThisWeekMonday ? ThisWeekPlan : null);

    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default)
        => Task.FromResult(ThisWeekPlan);

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

// ── Catalog reader stub ──────────────────────────────────────────────────────

/// <summary>
/// Catalog reader for the plantry-ri26 scenario: the one seeded product ("Flour") always resolves
/// to name "Flour" and default unit code "ea" — via both <see cref="SearchAsync"/> (the dish-search
/// hop) and <see cref="ResolveDefaultUnitCodesAsync"/> (the editor-hydration and week-load hops).
/// </summary>
internal sealed class StubUnitCodeCatalogProductReader : IMealPlanCatalogProductReader
{
    public Task<bool> ExistsAsync(Guid productId, CancellationToken ct = default) => Task.FromResult(true);

    public Task<bool> IsPlannableAsync(Guid productId, CancellationToken ct = default) => Task.FromResult(true);

    public Task<IReadOnlyList<MealPlanProductReadModel>> SearchAsync(
        string nameQuery, int maxResults = 20, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MealPlanProductReadModel>>(
            [new MealPlanProductReadModel(ProductUnitLabelFixture.FlourProductId, "Flour", "ea")]);

    public Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            productIds.ToDictionary(id => id, _ => "Flour"));

    public Task<IReadOnlyDictionary<Guid, string>> ResolveDefaultUnitCodesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            productIds.ToDictionary(id => id, _ => "ea"));
}
