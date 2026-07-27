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

namespace Plantry.Tests.Web.MealPlanning;

/// <summary>
/// L4 fragment tests for plantry-2ics: the "Use soon" badge must gate on unconsumed dishes, not on
/// <c>HasExpiringIngredients</c> alone. Drives the real Eat/UndoEat POST handlers (product dishes) so
/// the assertions exercise the actual <c>OnPostEatAsync</c>/<c>OnPostUndoEatAsync</c> → CellFragmentAsync
/// → enrichment-loop path, not a hand-rolled substitute — proving the fragment re-render (AC4) and undo
/// (AC5) genuinely flip the badge, not just that the VM would compute correctly in isolation.
/// </summary>
public sealed class MealCardConsumedBadgeTests
{
    private static string ExtractAntiforgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the page.");
        return match.Groups[1].Value;
    }

    private static FormUrlEncodedContent AntiforgeryForm(string token) => new(
        [new KeyValuePair<string, string>("__RequestVerificationToken", token)]);

    [Fact(DisplayName = "Single expiring product dish: badge shows while pending, clears on Eat (AC1/AC2/AC4), returns on Undo (AC5)")]
    public async Task Badge_Clears_On_Eat_And_Restores_On_Undo()
    {
        await using var factory = new ConsumedBadgeFactory(dishCount: 1);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ConsumedBadgeFixture.HouseholdId.ToString());

        // AC2: pending dish with expiring stock still shows the badge.
        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        Assert.Contains("mc-soon", pageHtml);
        var token = ExtractAntiforgeryToken(pageHtml);

        var dishId = factory.Repo.ProductDishIds[0];
        var eatUrl = $"/MealPlan?handler=Eat&plannedDishId={dishId:D}" +
                     $"&date={factory.Repo.TodayIso}&slotId={ConsumedBadgeFixture.LunchSlotId.Value:D}";

        // AC1/AC4: consuming the only dish clears the badge on the very next cell-fragment render.
        var eatResponse = await client.PostAsync(eatUrl, AntiforgeryForm(token));
        eatResponse.EnsureSuccessStatusCode();
        var eatHtml = await eatResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", eatHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mc-soon", eatHtml);

        // AC5: undoing the eat restores the badge for the now-pending dish.
        var undoUrl = $"/MealPlan?handler=UndoEat&plannedDishId={dishId:D}" +
                      $"&date={factory.Repo.TodayIso}&slotId={ConsumedBadgeFixture.LunchSlotId.Value:D}";
        var undoResponse = await client.PostAsync(undoUrl, AntiforgeryForm(token));
        undoResponse.EnsureSuccessStatusCode();
        var undoHtml = await undoResponse.Content.ReadAsStringAsync();
        Assert.Contains("mc-soon", undoHtml);
    }

    [Fact(DisplayName = "Multi-dish meal: badge reflects only the unconsumed dish (AC3) — persists until every expiring dish is eaten")]
    public async Task Badge_Reflects_Only_Unconsumed_Dishes_In_Mixed_Meal()
    {
        await using var factory = new ConsumedBadgeFactory(dishCount: 2);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ConsumedBadgeFixture.HouseholdId.ToString());

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        Assert.Contains("mc-soon", pageHtml);
        var token = ExtractAntiforgeryToken(pageHtml);

        var firstDishId = factory.Repo.ProductDishIds[0];
        var secondDishId = factory.Repo.ProductDishIds[1];

        // Eating only the first of two expiring dishes must NOT clear the card-level badge — the
        // second dish is still pending and still expiring (per-dish fix, not a whole-card gate).
        var firstEatResponse = await client.PostAsync(
            $"/MealPlan?handler=Eat&plannedDishId={firstDishId:D}" +
            $"&date={factory.Repo.TodayIso}&slotId={ConsumedBadgeFixture.LunchSlotId.Value:D}",
            AntiforgeryForm(token));
        firstEatResponse.EnsureSuccessStatusCode();
        var firstEatHtml = await firstEatResponse.Content.ReadAsStringAsync();
        Assert.Contains("mc-soon", firstEatHtml);

        // Eating the second (last remaining unconsumed, expiring) dish now clears the badge.
        var secondEatResponse = await client.PostAsync(
            $"/MealPlan?handler=Eat&plannedDishId={secondDishId:D}" +
            $"&date={factory.Repo.TodayIso}&slotId={ConsumedBadgeFixture.LunchSlotId.Value:D}",
            AntiforgeryForm(token));
        secondEatResponse.EnsureSuccessStatusCode();
        var secondEatHtml = await secondEatResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("mc-soon", secondEatHtml);
    }

    /// <summary>
    /// AC3 regression lock: distinguishes the per-dish fix from the whole-card alternative the ticket
    /// explicitly rejected (<c>enr.HasExpiringIngredients &amp;&amp; !allDishesDone</c>). Seeds one
    /// expiring dish and one fulfilled-but-NOT-expiring dish, both pending. Eating only the expiring
    /// dish leaves a single unconsumed dish (the non-expiring one) — the badge must clear. Under the
    /// whole-card gate this would still show (not all dishes are done yet); only the per-dish fix,
    /// which excludes the now-consumed dish from the aggregate, clears it.
    /// </summary>
    [Fact(DisplayName = "Multi-dish meal (AC3 regression lock): eating the expiring dish clears the badge even though the other (non-expiring) dish is still pending")]
    public async Task Badge_Clears_When_Last_Unconsumed_Dish_Is_Not_Expiring()
    {
        await using var factory = new ConsumedBadgeFactory(dishCount: 2, nonExpiringDishIndex: 1);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ConsumedBadgeFixture.HouseholdId.ToString());

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        // Both dishes pending: dish 0 is expiring, so the badge shows.
        Assert.Contains("mc-soon", pageHtml);
        var token = ExtractAntiforgeryToken(pageHtml);

        var expiringDishId = factory.Repo.ProductDishIds[0];

        var eatResponse = await client.PostAsync(
            $"/MealPlan?handler=Eat&plannedDishId={expiringDishId:D}" +
            $"&date={factory.Repo.TodayIso}&slotId={ConsumedBadgeFixture.LunchSlotId.Value:D}",
            AntiforgeryForm(token));
        eatResponse.EnsureSuccessStatusCode();
        var eatHtml = await eatResponse.Content.ReadAsStringAsync();

        // The sole remaining unconsumed dish (index 1) is not expiring — the badge must clear, even
        // though dish 1 is still pending (i.e. allDishesDone is false). A whole-card gate would keep
        // the badge here; only the per-dish fix clears it.
        Assert.DoesNotContain("mc-soon", eatHtml);
    }
}

// ── Fixture ─────────────────────────────────────────────────────────────────────

/// <summary>
/// WAF factory wiring a spy <see cref="IMealPlanEatWriter"/> (doubling as the cook-status reader, same
/// pattern as <see cref="EatActionFactory"/>) plus a stock reader reporting every seeded product as
/// expiring within the default 7-day horizon — so <c>PlanFulfillmentService</c> reports
/// <c>HasExpiringIngredients=true</c> for every pending dish until it is eaten. When
/// <paramref name="nonExpiringDishIndex"/> is given, that one product instead reports fulfilled stock
/// with a soonest expiry outside the horizon (AC3 regression-lock scenario).
/// </summary>
public sealed class ConsumedBadgeFactory(int dishCount, int? nonExpiringDishIndex = null) : WebApplicationFactory<Program>
{
    public ConsumedBadgeMealPlanRepo Repo { get; } = new(dishCount);
    public SpyEatWriter Writer { get; } = new();

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
            services.AddScoped<IMealSlotConfigRepository>(_ => new FakeSlotRepo(ConsumedBadgeFixture.SlotConfig));

            services.RemoveAll<IHouseholdMemberReader>();
            services.AddSingleton<IHouseholdMemberReader>(new FakeMemberReader([]));

            services.RemoveAll<IRecipeReadModel>();
            services.AddSingleton<IRecipeReadModel>(new FakeRecipeReader([]));

            services.RemoveAll<IMealPlanWeekReadModel>();
            services.AddSingleton<IMealPlanWeekReadModel>(new NullWeekReadModel());

            services.RemoveAll<IMealPlanCatalogProductReader>();
            services.AddSingleton<IMealPlanCatalogProductReader>(new FakeCatalogProductReaderW(existsResult: true));

            services.RemoveAll<IMealPlanEatWriter>();
            services.AddSingleton<IMealPlanEatWriter>(Writer);
            services.RemoveAll<IMealPlanCookStatusReader>();
            services.AddSingleton<IMealPlanCookStatusReader>(Writer);

            // Every seeded product reports 2 units in stock (matches each dish's servings, so it
            // never reads as "missing") with a soonest expiry 2 days out — inside the fake 7-day
            // expiring-soon horizon — so PlanFulfillmentService.ComputeDishFulfillmentAsync reports
            // HasExpiringIngredients=true for every pending dish.
            var nonExpiringIds = nonExpiringDishIndex is { } idx
                ? (IReadOnlyList<Guid>)[Repo.ProductIds[idx]]
                : [];
            var expiringIds = Repo.ProductIds.Where(id => !nonExpiringIds.Contains(id)).ToList();

            services.RemoveAll<IMealPlanStockReader>();
            services.AddSingleton<IMealPlanStockReader>(new ExpiringStockReader(expiringIds, nonExpiringIds));
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

            // Pin IClock to the same instant the fixture below derives "today" from (plantry-1w87), so the
            // SUT and the fixture never race two independent reads of the real system clock.
            services.RemoveAll<IClock>();
            services.AddScoped<IClock>(_ => new FixedClock(MealPlanningTestClock.Instant));
        });
    }
}

internal static class ConsumedBadgeFixture
{
    public static readonly Guid HouseholdId = Guid.Parse("77777777-0000-0000-0000-000000000007");

    private static readonly HouseholdId HhId = SharedKernel.HouseholdId.From(HouseholdId);
    public static readonly MealSlotConfig SlotConfig = MealSlotConfig.CreateWithDefaults(HhId, new FixedClock(MealPlanningTestClock.Instant));

    private static readonly List<MealSlot> OrderedSlots = [.. SlotConfig.Slots.OrderBy(s => s.Ordinal)];
    public static readonly MealSlotId LunchSlotId = OrderedSlots[1].Id;
}

/// <summary>
/// Meal plan repo backing the consumed-badge scenario: one meal, dated today, in the Lunch slot, with
/// <paramref name="dishCount"/> product dishes (servings=2 each) — one dish for the single-dish badge
/// test, two for the mixed-meal per-dish test (AC3).
/// </summary>
public sealed class ConsumedBadgeMealPlanRepo : IMealPlanRepository
{
    private readonly HouseholdId _household = SharedKernel.HouseholdId.From(ConsumedBadgeFixture.HouseholdId);
    public MealPlan Plan { get; }
    public DateOnly WeekMonday { get; }
    public DateOnly Today { get; }
    public string TodayIso => Today.ToString("yyyy-MM-dd");

    public IReadOnlyList<Guid> ProductIds { get; }
    public IReadOnlyList<Guid> ProductDishIds { get; }

    public ConsumedBadgeMealPlanRepo(int dishCount)
    {
        var clock = new FixedClock(MealPlanningTestClock.Instant);
        Today = DateOnly.FromDateTime(MealPlanningTestClock.Instant.UtcDateTime);
        WeekMonday = MealPlan.NormalizeToMonday(Today);

        var productIds = Enumerable.Range(0, dishCount).Select(_ => Guid.CreateVersion7()).ToList();
        ProductIds = productIds;

        Plan = MealPlan.Start(_household, WeekMonday, clock);
        Plan.AssignMeal(Today, ConsumedBadgeFixture.LunchSlotId,
            [.. productIds.Select(pid => new DishSpec(DishKind.Product, pid, 2))],
            null, "manual", Guid.Empty, clock);

        var meal = Plan.PlannedMeals.Single(m => m.MealSlotId == ConsumedBadgeFixture.LunchSlotId);
        ProductDishIds = [.. productIds.Select(pid => meal.PlannedDishes.Single(d => d.ProductId == pid).Id.Value)];
    }

    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default) =>
        Task.FromResult(householdId == _household && weekStart == WeekMonday ? Plan : null);

    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default) =>
        Task.FromResult(Plan);

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Stock reader reporting every product in <paramref name="expiringProductIds"/> as 2 units on hand
/// with a soonest expiry 2 days out — inside the fake 7-day expiring-soon horizon, so every one
/// resolves as both fulfilled (2 >= dish servings of 2) and expiring. Products in
/// <paramref name="nonExpiringButStockedProductIds"/> are also fulfilled (2 units on hand) but report
/// a soonest expiry 30 days out — outside the horizon, so <c>HasExpiringIngredients=false</c> for
/// those — enabling the AC3 regression-lock scenario (one expiring dish, one not, in the same meal).
/// </summary>
internal sealed class ExpiringStockReader(
    IReadOnlyList<Guid> expiringProductIds,
    IReadOnlyList<Guid> nonExpiringButStockedProductIds) : IMealPlanStockReader
{
    private static readonly Guid UnitId = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000e");

    public Task<MealPlanProductStock?> FindStockAsync(Guid productId, CancellationToken ct = default)
    {
        if (expiringProductIds.Contains(productId))
        {
            var soonestExpiry = DateOnly.FromDateTime(MealPlanningTestClock.Instant.UtcDateTime.AddDays(2));
            return Task.FromResult<MealPlanProductStock?>(
                new MealPlanProductStock(productId, 2m, UnitId, soonestExpiry));
        }

        if (nonExpiringButStockedProductIds.Contains(productId))
        {
            var farExpiry = DateOnly.FromDateTime(MealPlanningTestClock.Instant.UtcDateTime.AddDays(30));
            return Task.FromResult<MealPlanProductStock?>(
                new MealPlanProductStock(productId, 2m, UnitId, farExpiry));
        }

        return Task.FromResult<MealPlanProductStock?>(null);
    }
}
