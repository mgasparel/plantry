using Microsoft.AspNetCore.Mvc.Testing;
using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;
using Plantry.Web.MealPlanning;

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

    /// <summary>
    /// plantry-3aqj: regression guard for the recipe branch (Index.cshtml.cs:1154) of the
    /// consumed-badge fix. A pending, expiring recipe dish must still show the badge — pins that
    /// the fix isn't an over-eager suppression that always hides it.
    /// </summary>
    [Fact(DisplayName = "Recipe dish: badge shows while pending and expiring (plantry-3aqj)")]
    public async Task Recipe_Badge_Shows_While_Pending()
    {
        await using var factory = new RecipeConsumedBadgeFactory(cooked: false);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, EnrichmentFixture.HouseholdId.ToString());

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var pageHtml = await response.Content.ReadAsStringAsync();
        Assert.Contains("mc-soon", pageHtml);
    }

    /// <summary>
    /// plantry-3aqj: pins the recipe branch's <c>&amp;&amp; !dishIsConsumed</c> guard
    /// (Index.cshtml.cs:1154). Same expiring recipe dish as
    /// <see cref="Recipe_Badge_Shows_While_Pending"/>, but with cook status injected via
    /// <see cref="FixedCookStatusReader"/> so the dish reads as consumed — the badge must clear.
    /// Reverting the guard (restoring <c>if (dishEnr.HasExpiringIngredients) hasExpiring = true;</c>)
    /// must turn this test red while <see cref="Recipe_Badge_Shows_While_Pending"/> stays green.
    /// </summary>
    [Fact(DisplayName = "Recipe dish: badge clears once cooked, even though it is still expiring (plantry-3aqj)")]
    public async Task Recipe_Badge_Clears_When_Cooked()
    {
        await using var factory = new RecipeConsumedBadgeFactory(cooked: true);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, EnrichmentFixture.HouseholdId.ToString());

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var pageHtml = await response.Content.ReadAsStringAsync();
        // The recipe card must actually be on the page — otherwise the negative
        // assertion below would pass vacuously on an error/empty render.
        Assert.Contains("Test Recipe", pageHtml);
        Assert.DoesNotContain("mc-soon", pageHtml);
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
public sealed class ConsumedBadgeFactory(int dishCount, int? nonExpiringDishIndex = null) : MealPlanFragmentFactory
{
    public ConsumedBadgeMealPlanRepo Repo { get; } = new(dishCount);
    public SpyEatWriter Writer { get; } = new();

    protected override string FakeUserId => "00000000-0000-0000-0000-0000000000dd";
    protected override IMealPlanRepository MealPlanRepo => Repo;
    protected override IMealSlotConfigRepository SlotConfigRepo => new FakeSlotRepo(ConsumedBadgeFixture.SlotConfig);
    protected override IHouseholdMemberReader MemberReader => new FakeMemberReader([]);
    protected override IRecipeReadModel RecipeReadModel => new FakeRecipeReader([]);
    protected override IMealPlanCatalogProductReader CatalogProductReader =>
        new FakeCatalogProductReaderW(existsResult: true);
    protected override IMealPlanEatWriter? EatWriter => Writer;
    protected override IMealPlanCookStatusReader CookStatusReader => Writer;

    // Every seeded product reports 2 units in stock (matches each dish's servings, so it never
    // reads as "missing") with a soonest expiry 2 days out — inside the fake 7-day expiring-soon
    // horizon — so PlanFulfillmentService.ComputeDishFulfillmentAsync reports
    // HasExpiringIngredients=true for every pending dish.
    protected override IMealPlanStockReader StockReader
    {
        get
        {
            var nonExpiringIds = nonExpiringDishIndex is { } idx
                ? (IReadOnlyList<Guid>)[Repo.ProductIds[idx]]
                : [];
            var expiringIds = Repo.ProductIds.Where(id => !nonExpiringIds.Contains(id)).ToList();
            return new ExpiringStockReader(expiringIds, nonExpiringIds);
        }
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
            [.. productIds.Select(pid => DishSpec.ForProduct(pid, 2m, ExpiringStockReader.UnitId))],
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
    internal static readonly Guid UnitId = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000e");

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

// ── plantry-3aqj: recipe-branch fixture ────────────────────────────────────────

/// <summary>
/// Meal plan repo seeding one recipe dish (mirrors <see cref="EnrichmentMealPlanRepo"/>), but also
/// exposes the seeded dish's id so a test can key a <see cref="FixedCookStatusReader"/> double to
/// it and target it as the "consumed" dish.
/// </summary>
public sealed class RecipeConsumedBadgeMealPlanRepo : IMealPlanRepository
{
    private readonly MealPlan _plan;
    public Guid RecipeDishId { get; }

    public RecipeConsumedBadgeMealPlanRepo(Guid recipeId)
    {
        var hhId = SharedKernel.HouseholdId.From(EnrichmentFixture.HouseholdId);
        var today = DateOnly.FromDateTime(MealPlanningTestClock.Instant.UtcDateTime);
        var monday = MealPlan.NormalizeToMonday(today);
        var clock = new FixedClock(MealPlanningTestClock.Instant);
        _plan = MealPlan.Start(hhId, monday, clock);
        _plan.AssignMeal(monday, EnrichmentFixture.SlotId, [new DishSpec(DishKind.Recipe, recipeId, 2)],
            null, "manual", Guid.Empty, clock);

        var meal = _plan.PlannedMeals.Single(m => m.MealSlotId == EnrichmentFixture.SlotId);
        RecipeDishId = meal.PlannedDishes.Single(d => d.RecipeId == recipeId).Id.Value;
    }

    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
        => Task.FromResult<MealPlan?>(_plan);

    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default)
        => Task.FromResult(_plan);

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// WAF factory for the recipe branch (Index.cshtml.cs:1154) of the consumed-badge fix (plantry-3aqj).
/// Reuses <see cref="EnrichmentFixture"/>'s ids and the standard 80%/$12.50/expiring enrichment case
/// (via <see cref="EnrichmentRecipeReader"/> and <see cref="FakeEnrichmentWeekReadModel"/>, both
/// defined in MealCardEnrichmentTests.cs), and injects cook status directly via
/// <see cref="FixedCookStatusReader"/> — there is no real "Cook" write double in this test project
/// the way Eat/UndoEat has <see cref="SpyEatWriter"/>, matching how
/// <c>MealCardCookStripTests.cs</c> already tests the cook strip's rendering contract without a
/// real <c>CookEvent</c> write path.
/// </summary>
public sealed class RecipeConsumedBadgeFactory(bool cooked) : MealPlanFragmentFactory
{
    public RecipeConsumedBadgeMealPlanRepo Repo { get; } = new(EnrichmentFixture.RecipeId);

    protected override string FakeUserId => "00000000-0000-0000-0000-0000000000ee";
    protected override IMealPlanRepository MealPlanRepo => Repo;
    protected override IMealSlotConfigRepository SlotConfigRepo => new FakeSlotRepo(EnrichmentFixture.SlotConfig);
    protected override IHouseholdMemberReader MemberReader => new FakeMemberReader([]);

    protected override IRecipeReadModel RecipeReadModel => new EnrichmentRecipeReader(
        EnrichmentFixture.RecipeId,
        new RecipeDishEnrichment(80, 12.50m, false, true));

    protected override IMealPlanWeekReadModel WeekReadModel =>
        new FakeEnrichmentWeekReadModel(useExpiring: true, fulfillmentPct: 80, totalCost: 12.50m);

    protected override IMealPlanCatalogProductReader CatalogProductReader =>
        new FakeCatalogProductReaderW(existsResult: true);

    protected override IMealPlanCookStatusReader CookStatusReader => new FixedCookStatusReader(
        cooked
            ? new Dictionary<Guid, DishCookStatus> { [Repo.RecipeDishId] = new DishCookStatus(MealPlanningTestClock.Instant.AddMinutes(-10)) }
            : new Dictionary<Guid, DishCookStatus>());
}
