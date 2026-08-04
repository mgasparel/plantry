using Microsoft.AspNetCore.Mvc.Testing;
using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;
using System.Text.Json;

namespace Plantry.Tests.Web.MealPlanning;

/// <summary>
/// L4 regression coverage for plantry-ri26: a product dish planned onto a meal was always labelled
/// with the hardcoded word "servings" (editor stepper, dish search) or rendered with NO unit at all
/// (Cook strip's pending "Eat" button and done "Eaten ·" row) — regardless of the product's actually
/// configured default unit. The fix threads a real unit CODE (e.g. "ea") through every one of those
/// surfaces. Covers the three server-rendered/hydrated hops:
///   1. The Cook strip's pending "Eat" button — the product's configured unit — and, since plantry-vqa7,
///      the done "Eaten ·" row's JOURNAL-derived consumed unit (_MealCard.cshtml).
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

    [Fact(DisplayName = "GET /MealPlan: a done product dish's Cook-strip row shows the JOURNAL unit its consumed quantity was denominated in, not the product's configured default")]
    public async Task DoneProductDish_ShowsJournalConsumedUnit_NotProductDefault()
    {
        await using var factory = new ProductUnitLabelFactory();
        var client = CreateClient(factory);

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Breakfast's already-eaten product dish (3 units) shows "Eaten · 3 g" — not the pre-fix
        // bare "Eaten · 3" with no unit at all. The done row is denominated in the JOURNAL row's own
        // unit ("g", plantry-vqa7's DishCookStatus.ConsumedUnitId) — NOT the product's configured
        // default unit ("ea", which the still-pending Lunch dish's Eat button below proves is what
        // Flour actually defaults to). A regression that reused ResolveDefaultUnitCodesAsync/UnitCode
        // for the done row instead of the journal-derived ConsumedUnitCode would render "Eaten · 3 ea"
        // here — exactly what the negative assertion below catches.
        Assert.Contains("Eaten · 3 g", html);
        Assert.DoesNotContain("Eaten · 3 ea", html);
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

    /// <summary>
    /// A journal unit deliberately distinct from every id that resolves to the product's own default
    /// unit code, resolving to a
    /// DIFFERENT display code ("g", not "ea") via
    /// <see cref="StubUnitCodeCatalogProductReader.ResolveUnitCodesAsync"/> — so the done row's
    /// ConsumedUnitCode is provably read from the journal unit id, not silently reusing the product's
    /// own configured default unit code (both of which happen to be "ea" for Flour, which would let a
    /// `ConsumedUnitCode`/`UnitCode` mix-up pass unnoticed).
    /// </summary>
    public static readonly Guid GramsUnitId = Guid.Parse("99999999-0000-0000-0000-000000000009");
}

// ── Factory ───────────────────────────────────────────────────────────────────

/// <summary>
/// WAF factory wiring a plan with a done+pending product dish (plus a control recipe dish) and a
/// catalog reader that resolves the product's unit code to "ea". Mirrors
/// <c>MealCardCookStripFactory</c>'s service wiring (WeekGridFragmentTests.cs /
/// MealCardCookStripTests.cs) — this suite differs only in the plan repo and catalog reader.
/// </summary>
public sealed class ProductUnitLabelFactory : MealPlanFragmentFactory
{
    public ProductUnitLabelMealPlanRepo Repo { get; } = new();

    protected override string FakeUserId => "00000000-0000-0000-0000-0000000000dd";
    protected override IMealPlanRepository MealPlanRepo => Repo;
    protected override IMealSlotConfigRepository SlotConfigRepo => new FakeSlotRepo(ProductUnitLabelFixture.SlotConfig);
    protected override IHouseholdMemberReader MemberReader => new FakeMemberReader([]);
    protected override IRecipeReadModel RecipeReadModel => new FakeRecipeReader([]);

    // The port under test: resolves the one product's unit code to "ea" everywhere
    // (SearchAsync, ResolveDefaultUnitCodesAsync) — plantry-ri26.
    protected override IMealPlanCatalogProductReader CatalogProductReader => new StubUnitCodeCatalogProductReader();

    protected override IMealPlanCookStatusReader CookStatusReader => new FixedCookStatusReader(
        new Dictionary<Guid, DishCookStatus>
        {
            [Repo.DoneRecipeDishId] = new DishCookStatus(MealPlanningTestClock.Instant.AddMinutes(-30)),
            // plantry-vqa7: the done row now displays the ACTUAL eaten quantity — 3, the planned
            // amount, since this fixture's "eat" was never adjusted; the point under test here is the
            // UNIT code beside it, which is why this suite exists. Keyed to GramsUnitId (resolves to
            // "g"), deliberately DIFFERENT from Flour's own configured default unit ("ea", proven by
            // the still-pending Lunch dish's Eat button) — proves the done row reads the journal's
            // own unit, not the product's default.
            [Repo.DoneProductDishId] = new DishCookStatus(
                MealPlanningTestClock.Instant.AddMinutes(-20), 3m, ProductUnitLabelFixture.GramsUnitId),
        });
}

// ── Plan repo ─────────────────────────────────────────────────────────────────

/// <summary>
/// Meal plan repo for the plantry-ri26 unit-label scenario: Breakfast carries a done recipe dish
/// (control) plus a done product dish; Lunch carries one still-pending product dish. All dated the
/// current week's Monday.
/// </summary>
public sealed class ProductUnitLabelMealPlanRepo : IMealPlanRepository
{
    public Task<IReadOnlyDictionary<Guid, PlannedMealSlotInfo>> FindSlotLabelsAsync(
        IReadOnlyList<Guid> plannedMealIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, PlannedMealSlotInfo>>(new Dictionary<Guid, PlannedMealSlotInfo>());

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
                DishSpec.ForProduct(ProductUnitLabelFixture.FlourProductId, 3m, Guid.NewGuid()),
            ],
            null, "manual", Guid.Empty, _clock);

        // Lunch: one still-pending product dish.
        ThisWeekPlan.AssignMeal(ThisWeekMonday, ProductUnitLabelFixture.LunchSlotId,
            [DishSpec.ForProduct(ProductUnitLabelFixture.FlourProductId, 5m, Guid.NewGuid())],
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

    /// <summary>
    /// plantry-vqa7: resolves <see cref="ProductUnitLabelFixture.GramsUnitId"/> to "g" and every other
    /// requested id to "ea" — id-aware (not a blanket "ea" for everything) so a done row's
    /// ConsumedUnitCode can be proven to come from the JOURNAL unit id, distinct from the product's
    /// own configured default unit code.
    /// </summary>
    public Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
        IReadOnlyCollection<Guid> unitIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            unitIds.ToDictionary(id => id, id => id == ProductUnitLabelFixture.GramsUnitId ? "g" : "ea"));
}
