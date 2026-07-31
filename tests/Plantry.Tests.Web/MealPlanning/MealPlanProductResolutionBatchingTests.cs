using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.MealPlanning;

/// <summary>
/// L4 regression coverage for plantry-vj6z: <c>LoadWeekAsync</c> used to resolve product-dish
/// names/unit codes in a per-meal batch (two <c>catalogReader</c> round trips for every meal that
/// contained a product dish), instead of one week-wide batch. This suite proves the fix is both
/// correct (AC2/AC3/AC4 — every product dish in every meal still renders its right name/unit,
/// including a product that recurs across meals) and actually flat in meal count (AC1 — the
/// resolver is called at most once per <c>LoadWeekAsync</c> invocation, verified by a
/// call-counting catalog reader stub rather than by inference from behaviour alone). The same
/// call-count policing covers the two later batched lookups that landed in the same spot: the
/// product-dish photo-inheritance lookup (plantry-f4dt) and the done-dish consumed-unit-code
/// lookup (plantry-vqa7).
/// </summary>
public sealed class MealPlanProductResolutionBatchingTests
{
    private static HttpClient CreateClient(WebApplicationFactory<Program> factory, Guid householdId)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, householdId.ToString());
        return client;
    }

    [Fact(DisplayName = "GET /MealPlan: product dishes across 3+ meals resolve via exactly one week-wide batch call each")]
    public async Task ThreeMealsWithProductDishes_ResolvesInOneBatchEach()
    {
        await using var factory = new ProductBatchingFactory();
        var client = CreateClient(factory, ProductBatchingFixture.HouseholdId);

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // AC1: each resolver called at most once for the whole week load, regardless of how many
        // of the three meals (Breakfast, Lunch, Dinner) contain product dishes.
        Assert.Equal(1, factory.CatalogReader.ResolveNamesCallCount);
        Assert.Equal(1, factory.CatalogReader.ResolveUnitCodesCallCount);
        // plantry-vqa7: the consumed-unit-code lookup for the two DONE product dishes (Butter in
        // Breakfast, Oil in Dinner — two meals, two distinct unit ids) sits in the same batched
        // spot — a per-meal or per-dish regression would count 2 here, not 1.
        Assert.Equal(1, factory.CatalogReader.ResolveUnitCodesByIdCallCount);
        // plantry-f4dt: the product-dish photo-inheritance lookup sits in the exact same batched
        // spot as the calls above — must not regress to a per-meal round trip either.
        Assert.Equal(1, factory.RecipeReader.FindSoleYieldPhotoCallCount);

        // AC3: Flour is planned in BOTH Breakfast and Lunch — the Distinct() union that feeds the
        // single batch call must not drop either occurrence's rendering. A single Assert.Contains
        // would stay green even if only one of the two occurrences resolved, so this counts both.
        Assert.Equal(2, Regex.Matches(html, "<span class=\"md-name\">Flour</span>").Count);
        // AC2: Sugar only appears in the third meal (Dinner) — proving the batch is week-wide, not
        // just wide enough to cover two meals.
        Assert.Contains("<span class=\"md-name\">Sugar</span>", html);

        // Every product dish renders its real unit code, in every one of the three meals — the "?"
        // fallback would appear if the week-wide dictionaries were empty or mis-keyed.
        Assert.Contains("act-srv\">2 ea</span>", html);   // Breakfast: Flour, 2
        Assert.Contains("act-srv\">1 ea</span>", html);   // Lunch: Flour, 1
        Assert.Contains("act-srv\">1 g</span>", html);    // Dinner: Sugar, 1
        Assert.DoesNotContain("act-srv\">2 ?</span>", html);
    }

    [Fact(DisplayName = "GET /MealPlan: a week with zero product dishes performs zero product-resolution calls")]
    public async Task ZeroProductDishes_PerformsZeroCalls()
    {
        await using var factory = new RecipeOnlyBatchingFactory();
        var client = CreateClient(factory, ProductBatchingFixture.HouseholdId);

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();

        // AC4: no product dishes anywhere in the week -> zero product-resolution round trips.
        Assert.Equal(0, factory.CatalogReader.ResolveNamesCallCount);
        Assert.Equal(0, factory.CatalogReader.ResolveUnitCodesCallCount);
        // plantry-vqa7: no done product dish -> no consumed unit ids -> the `consumedUnitIds.Count
        // > 0` guard must skip the consumed-unit lookup entirely.
        Assert.Equal(0, factory.CatalogReader.ResolveUnitCodesByIdCallCount);
        // plantry-f4dt: the `allProductDishIds.Count > 0` guard at Index.cshtml.cs:1177 must skip
        // the photo-inheritance lookup too when there are zero product dishes.
        Assert.Equal(0, factory.RecipeReader.FindSoleYieldPhotoCallCount);
    }
}

// ── Fixture ───────────────────────────────────────────────────────────────────

internal static class ProductBatchingFixture
{
    public static readonly Guid HouseholdId = Guid.Parse("88888888-0000-0000-0000-000000000008");

    private static readonly HouseholdId HhId = SharedKernel.HouseholdId.From(HouseholdId);
    public static readonly MealSlotConfig SlotConfig =
        MealSlotConfig.CreateWithDefaults(HhId, new FixedClock(MealPlanningTestClock.Instant));

    private static readonly List<MealSlot> OrderedSlots = [.. SlotConfig.Slots.OrderBy(s => s.Ordinal)];
    public static readonly MealSlotId BreakfastSlotId = OrderedSlots[0].Id;
    public static readonly MealSlotId LunchSlotId = OrderedSlots[1].Id;
    public static readonly MealSlotId DinnerSlotId = OrderedSlots[2].Id;

    /// <summary>Planned in both Breakfast and Lunch — the same-product-in-two-meals case (AC3).</summary>
    public static readonly Guid FlourProductId = Guid.CreateVersion7();

    /// <summary>Planned only in Dinner — the third distinct meal that a per-meal (rather than
    /// week-wide) batch would still technically cover, but which proves the union spans all
    /// meals rather than being coincidentally wide enough for two (AC2).</summary>
    public static readonly Guid SugarProductId = Guid.CreateVersion7();

    /// <summary>Planned in Breakfast and already DONE (plantry-vqa7) — its consumed unit id is
    /// what makes <c>LoadWeekAsync</c>'s fourth batched catalog call
    /// (<c>ResolveUnitCodesAsync</c>) fire at all.</summary>
    public static readonly Guid ButterProductId = Guid.CreateVersion7();

    /// <summary>Planned in Dinner and already DONE (plantry-vqa7) — a second done product dish in
    /// a DIFFERENT meal, so a regression to a per-meal consumed-unit lookup surfaces as two calls
    /// rather than one.</summary>
    public static readonly Guid OilProductId = Guid.CreateVersion7();

    /// <summary>The unit Butter's consumed quantity is denominated in (plantry-vqa7).</summary>
    public static readonly Guid EachUnitId = Guid.Parse("eeeeeeee-1111-0000-0000-00000000000e");

    /// <summary>The unit Oil's consumed quantity is denominated in — deliberately distinct from
    /// <see cref="EachUnitId"/> so the single batched call must carry BOTH ids (plantry-vqa7).</summary>
    public static readonly Guid GramUnitId = Guid.Parse("99999999-1111-0000-0000-000000000009");
}

// ── Factory: three meals, two of which share a product ──────────────────────────

/// <summary>
/// WAF factory wiring a plan with product dishes in all three default slots (Breakfast, Lunch,
/// Dinner) — Flour in Breakfast and Lunch, Sugar in Dinner — plus a call-counting catalog reader.
/// </summary>
public sealed class ProductBatchingFactory : MealPlanFragmentFactory
{
    public CountingCatalogProductReader CatalogReader { get; } = new();
    public CountingYieldPhotoRecipeReader RecipeReader { get; } = new();
    public ProductBatchingMealPlanRepo Repo { get; } = new();

    protected override string FakeUserId => "00000000-0000-0000-0000-0000000000ee";
    protected override IMealPlanRepository MealPlanRepo => Repo;
    protected override IMealSlotConfigRepository SlotConfigRepo => new FakeSlotRepo(ProductBatchingFixture.SlotConfig);
    protected override IHouseholdMemberReader MemberReader => new FakeMemberReader([]);
    protected override IMealPlanCatalogProductReader CatalogProductReader => CatalogReader;
    protected override IRecipeReadModel RecipeReadModel => RecipeReader;

    // plantry-vqa7: two DONE product dishes in two DIFFERENT meals, carrying two DISTINCT consumed
    // unit ids — the shape that makes the week-wide ResolveUnitCodesAsync batch fire exactly once,
    // and that would surface a per-meal regression as two calls rather than one.
    protected override IMealPlanCookStatusReader CookStatusReader =>
        new FixedCookStatusReader(new Dictionary<Guid, DishCookStatus>
        {
            [Repo.ButterDishId] = new(MealPlanningTestClock.Instant, 2m, ProductBatchingFixture.EachUnitId),
            [Repo.OilDishId] = new(MealPlanningTestClock.Instant, 1m, ProductBatchingFixture.GramUnitId),
        });
}

// ── Factory: recipe-only week (AC4) ──────────────────────────────────────────

/// <summary>Same wiring as <see cref="ProductBatchingFactory"/> but the plan has zero product
/// dishes — used to prove AC4 (no product dishes -> zero resolver calls).</summary>
public sealed class RecipeOnlyBatchingFactory : MealPlanFragmentFactory
{
    public CountingCatalogProductReader CatalogReader { get; } = new();
    public CountingYieldPhotoRecipeReader RecipeReader { get; } = new();
    public RecipeOnlyMealPlanRepo Repo { get; } = new();

    protected override string FakeUserId => "00000000-0000-0000-0000-0000000000ff";
    protected override IMealPlanRepository MealPlanRepo => Repo;
    protected override IMealSlotConfigRepository SlotConfigRepo => new FakeSlotRepo(ProductBatchingFixture.SlotConfig);
    protected override IHouseholdMemberReader MemberReader => new FakeMemberReader([]);
    protected override IMealPlanCatalogProductReader CatalogProductReader => CatalogReader;
    protected override IRecipeReadModel RecipeReadModel => RecipeReader;

    protected override IMealPlanCookStatusReader CookStatusReader =>
        new FixedCookStatusReader(new Dictionary<Guid, DishCookStatus>());
}

// ── Plan repos ────────────────────────────────────────────────────────────────

/// <summary>
/// Meal plan repo for the plantry-vj6z batching scenario: Flour is planned in both Breakfast and
/// Lunch (same product, two meals); Sugar is planned only in Dinner (a third, distinct meal).
/// Breakfast and Dinner additionally carry one DONE product dish each (Butter and Oil,
/// plantry-vqa7) to drive the consumed-unit-code batch. All dated the current week's Monday.
/// </summary>
public sealed class ProductBatchingMealPlanRepo : IMealPlanRepository
{
    private static readonly IClock _clock = new FixedClock(MealPlanningTestClock.Instant);

    public MealPlan ThisWeekPlan { get; }
    public DateOnly ThisWeekMonday { get; }

    /// <summary>Breakfast's DONE Butter dish (plantry-vqa7) — drives the consumed-unit batch.</summary>
    public Guid ButterDishId { get; private set; }

    /// <summary>Dinner's DONE Oil dish (plantry-vqa7) — a second done dish in a different meal.</summary>
    public Guid OilDishId { get; private set; }

    public ProductBatchingMealPlanRepo()
    {
        var hhId = SharedKernel.HouseholdId.From(ProductBatchingFixture.HouseholdId);
        var today = DateOnly.FromDateTime(MealPlanningTestClock.Instant.UtcDateTime);
        ThisWeekMonday = MealPlan.NormalizeToMonday(today);

        ThisWeekPlan = MealPlan.Start(hhId, ThisWeekMonday, _clock);

        ThisWeekPlan.AssignMeal(ThisWeekMonday, ProductBatchingFixture.BreakfastSlotId,
            [
                new DishSpec(DishKind.Product, ProductBatchingFixture.FlourProductId, 2),
                new DishSpec(DishKind.Product, ProductBatchingFixture.ButterProductId, 2),
            ],
            null, "manual", Guid.Empty, _clock);

        ThisWeekPlan.AssignMeal(ThisWeekMonday, ProductBatchingFixture.LunchSlotId,
            [new DishSpec(DishKind.Product, ProductBatchingFixture.FlourProductId, 1)],
            null, "manual", Guid.Empty, _clock);

        ThisWeekPlan.AssignMeal(ThisWeekMonday, ProductBatchingFixture.DinnerSlotId,
            [
                new DishSpec(DishKind.Product, ProductBatchingFixture.SugarProductId, 1),
                new DishSpec(DishKind.Product, ProductBatchingFixture.OilProductId, 1),
            ],
            null, "manual", Guid.Empty, _clock);

        // Resolve the two done dishes' ids post-construction, same pattern as
        // ProductUnitLabelMealPlanRepo (MealPlanProductUnitLabelTests.cs).
        var breakfast = ThisWeekPlan.PlannedMeals.Single(m => m.MealSlotId == ProductBatchingFixture.BreakfastSlotId);
        ButterDishId = breakfast.PlannedDishes.Single(d => d.ProductId == ProductBatchingFixture.ButterProductId).Id.Value;

        var dinner = ThisWeekPlan.PlannedMeals.Single(m => m.MealSlotId == ProductBatchingFixture.DinnerSlotId);
        OilDishId = dinner.PlannedDishes.Single(d => d.ProductId == ProductBatchingFixture.OilProductId).Id.Value;
    }

    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
        => Task.FromResult<MealPlan?>(weekStart == ThisWeekMonday ? ThisWeekPlan : null);

    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default)
        => Task.FromResult(ThisWeekPlan);

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Meal plan repo with a single recipe dish and zero product dishes — AC4.</summary>
public sealed class RecipeOnlyMealPlanRepo : IMealPlanRepository
{
    private static readonly IClock _clock = new FixedClock(MealPlanningTestClock.Instant);

    public MealPlan ThisWeekPlan { get; }
    public DateOnly ThisWeekMonday { get; }
    public Guid PancakesRecipeId { get; } = Guid.CreateVersion7();

    public RecipeOnlyMealPlanRepo()
    {
        var hhId = SharedKernel.HouseholdId.From(ProductBatchingFixture.HouseholdId);
        var today = DateOnly.FromDateTime(MealPlanningTestClock.Instant.UtcDateTime);
        ThisWeekMonday = MealPlan.NormalizeToMonday(today);

        ThisWeekPlan = MealPlan.Start(hhId, ThisWeekMonday, _clock);

        ThisWeekPlan.AssignMeal(ThisWeekMonday, ProductBatchingFixture.BreakfastSlotId,
            [new DishSpec(DishKind.Recipe, PancakesRecipeId, 2)],
            null, "manual", Guid.Empty, _clock);
    }

    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
        => Task.FromResult<MealPlan?>(weekStart == ThisWeekMonday ? ThisWeekPlan : null);

    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default)
        => Task.FromResult(ThisWeekPlan);

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

// ── Catalog reader stub ──────────────────────────────────────────────────────

/// <summary>
/// Catalog reader for the plantry-vj6z batching scenario. Counts how many times
/// <see cref="ResolveNamesAsync"/>, <see cref="ResolveDefaultUnitCodesAsync"/>, and the
/// consumed-unit resolver <see cref="ResolveUnitCodesAsync"/> (plantry-vqa7's third batched
/// resolver on this port) are invoked, so tests can assert the week-wide batching directly (AC1)
/// rather than only inferring it from rendered output. Resolves Flour -> "ea" and Sugar -> "g";
/// any other id falls back to "Unknown".
/// </summary>
public sealed class CountingCatalogProductReader : IMealPlanCatalogProductReader
{
    public int ResolveNamesCallCount { get; private set; }
    public int ResolveUnitCodesCallCount { get; private set; }

    /// <summary>Counts <see cref="ResolveUnitCodesAsync"/> (consumed-unit ids, plantry-vqa7) —
    /// named distinctly from <see cref="ResolveUnitCodesCallCount"/>, which counts
    /// <see cref="ResolveDefaultUnitCodesAsync"/> (product default units).</summary>
    public int ResolveUnitCodesByIdCallCount { get; private set; }

    public Task<bool> ExistsAsync(Guid productId, CancellationToken ct = default) => Task.FromResult(true);

    public Task<bool> IsPlannableAsync(Guid productId, CancellationToken ct = default) => Task.FromResult(true);

    public Task<IReadOnlyList<MealPlanProductReadModel>> SearchAsync(
        string nameQuery, int maxResults = 20, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MealPlanProductReadModel>>([]);

    public Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        ResolveNamesCallCount++;
        return Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            productIds.ToDictionary(id => id, ResolveName));
    }

    public Task<IReadOnlyDictionary<Guid, string>> ResolveDefaultUnitCodesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        ResolveUnitCodesCallCount++;
        return Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            productIds.ToDictionary(id => id, ResolveUnitCode));
    }

    public Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
        IReadOnlyCollection<Guid> unitIds, CancellationToken ct = default)
    {
        ResolveUnitCodesByIdCallCount++;
        return Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            unitIds.ToDictionary(id => id, id => id == ProductBatchingFixture.GramUnitId ? "g" : "ea"));
    }

    private static string ResolveName(Guid id) =>
        id == ProductBatchingFixture.FlourProductId ? "Flour"
        : id == ProductBatchingFixture.SugarProductId ? "Sugar"
        : "Unknown product";

    private static string ResolveUnitCode(Guid id) =>
        id == ProductBatchingFixture.FlourProductId ? "ea"
        : id == ProductBatchingFixture.SugarProductId ? "g"
        : "?";
}

// ── Recipe reader stub ───────────────────────────────────────────────────────

/// <summary>
/// <see cref="IRecipeReadModel"/> stub for the plantry-f4dt photo-inheritance batching regression:
/// every method other than <see cref="FindSoleYieldPhotoRecipeIdsAsync"/> returns "no data" (this
/// suite's plans carry no ghost dishes or unfulfillability checks that would need them — same
/// shape as <c>YieldPhotoRecipeReader</c> in MealCardProductPhotoTests.cs), and the photo lookup
/// counts its own calls so AC1/AC4 can assert the batching directly rather than infer it.
/// </summary>
public sealed class CountingYieldPhotoRecipeReader : IRecipeReadModel
{
    public int FindSoleYieldPhotoCallCount { get; private set; }

    public Task<RecipeReadModel?> GetByIdAsync(Guid recipeId, CancellationToken ct = default) =>
        Task.FromResult<RecipeReadModel?>(null);

    public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string nameQuery, int maxResults = 20, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RecipeReadModel>>([]);

    public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid recipeId, int servings, DateOnly today, CancellationToken ct = default) =>
        Task.FromResult<RecipeDishEnrichment?>(null);

    public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid recipeId, int servings, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RecipeMissingIngredient>>([]);

    public Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyDictionary<Guid, Guid>> FindSoleYieldPhotoRecipeIdsAsync(
        IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        FindSoleYieldPhotoCallCount++;
        return Task.FromResult<IReadOnlyDictionary<Guid, Guid>>(new Dictionary<Guid, Guid>());
    }
}
