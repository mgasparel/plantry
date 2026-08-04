using Microsoft.AspNetCore.Mvc.Testing;
using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.MealPlanning;

/// <summary>
/// L4 fragment tests for plantry-f4dt: a product-only meal (no Recipe-kind dish) whose first
/// product dish's product is the sole declared cook-yield of a photo-bearing recipe renders that
/// recipe's photo in the <c>mc-photo</c> tile — resolved LIVE via
/// <see cref="IRecipeReadModel.FindSoleYieldPhotoRecipeIdsAsync"/>, never a duplicated copy. Every
/// other case (zero/multiple producer-recipes, a photo-less sole producer, or a meal containing any
/// Recipe-kind dish) keeps the existing gradient placeholder — unchanged (plantry-tyvg).
/// </summary>
public sealed class MealCardProductPhotoTests
{
    private static HttpClient CreateClient(MealCardProductPhotoFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ProductPhotoFixture.HouseholdId.ToString());
        return client;
    }

    [Fact(DisplayName = "GET /MealPlan: product-only meal shows the sole producer-recipe's photo (plantry-f4dt)")]
    public async Task ProductOnlyMeal_Shows_Inherited_Photo_When_Sole_Producer_Has_Photo()
    {
        await using var factory = new MealCardProductPhotoFactory();
        var client = CreateClient(factory);

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        var imgTag = $"<img class=\"mc-photo-img\" src=\"/Recipes/{ProductPhotoFixture.SoleProducerRecipeId}?handler=Photo\"";
        Assert.Contains(imgTag, html);
        Assert.Contains("alt=\"Leftover Lasagna\"", html);

        // Exactly ONE occurrence: Breakfast (product-only) resolves the photo; Dinner ALSO plans the
        // same photo-inheriting product alongside a Recipe-kind dish, but must not also render it
        // (that is behavior rule 1, asserted precisely by the next test) — a second occurrence here
        // would mean the recipe-kind-wins rule silently broke.
        Assert.Equal(1, CountOccurrences(html, imgTag));
    }

    [Fact(DisplayName = "GET /MealPlan: product-only meal keeps the placeholder when its product has no inherited photo (plantry-f4dt)")]
    public async Task ProductOnlyMeal_Shows_Placeholder_When_No_Inherited_Photo()
    {
        await using var factory = new MealCardProductPhotoFactory();
        var client = CreateClient(factory);

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Lunch's product (NoInheritanceProductId, resolved name "Plain Product") never appears as a
        // key in the fake's photo dictionary, so its dish must never render as a photo — the only way
        // "Plain Product" could appear as an <img> alt is if the placeholder fallback broke.
        Assert.DoesNotContain("alt=\"Plain Product\"", html);
    }

    [Fact(DisplayName = "GET /MealPlan: a meal with a Recipe-kind dish stays on the placeholder even when a same-meal product dish would resolve a photo (plantry-f4dt behavior rule 1)")]
    public async Task MealWithRecipeDish_Ignores_ProductDish_Photo_Inheritance()
    {
        await using var factory = new MealCardProductPhotoFactory();
        var client = CreateClient(factory);

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Dinner has BOTH a (photo-less) Recipe dish AND the photo-inheriting product dish. The
        // Recipe-kind dish must win the primary-photo slot regardless (behavior rule 1) — since it
        // has no photo, Dinner's tile must be the gradient placeholder, so the photo endpoint's URL
        // must render exactly once in the whole page (from Breakfast alone, asserted above) rather
        // than twice (which the previous test's exact-count assertion already pins down).
        var imgTag = $"<img class=\"mc-photo-img\" src=\"/Recipes/{ProductPhotoFixture.SoleProducerRecipeId}?handler=Photo\"";
        Assert.Equal(1, CountOccurrences(html, imgTag));
    }

    [Fact(DisplayName = "GET /MealPlan: product-only meal keeps the placeholder when the FIRST product dish resolves no photo, even though a later one would (plantry-f4dt behavior rule 2)")]
    public async Task ProductOnlyMeal_FirstDishNoPhoto_DoesNotFallThroughToLaterDish()
    {
        await using var factory = new MealCardProductPhotoFactory();
        var client = CreateClient(factory);

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Tuesday Breakfast plans NoInheritanceProductId FIRST and PhotoInheritingProductId second.
        // The "first-or-placeholder" rule must stop at the first dish — the count of the photo
        // endpoint's URL across the WHOLE page must stay at exactly 1 (Monday Breakfast alone); a
        // count of 2 would mean the second product dish's photo leaked through instead of the
        // placeholder.
        var imgTag = $"<img class=\"mc-photo-img\" src=\"/Recipes/{ProductPhotoFixture.SoleProducerRecipeId}?handler=Photo\"";
        Assert.Equal(1, CountOccurrences(html, imgTag));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}

// ── Fixture ───────────────────────────────────────────────────────────────────

internal static class ProductPhotoFixture
{
    public static readonly Guid HouseholdId = Guid.Parse("99999999-0000-0000-0000-000000000009");

    private static readonly HouseholdId HhId = SharedKernel.HouseholdId.From(HouseholdId);
    public static readonly MealSlotConfig SlotConfig =
        MealSlotConfig.CreateWithDefaults(HhId, new FixedClock(MealPlanningTestClock.Instant));

    private static readonly List<MealSlot> OrderedSlots = [.. SlotConfig.Slots.OrderBy(s => s.Ordinal)];
    public static readonly MealSlotId BreakfastSlotId = OrderedSlots[0].Id;
    public static readonly MealSlotId LunchSlotId = OrderedSlots[1].Id;
    public static readonly MealSlotId DinnerSlotId = OrderedSlots[2].Id;

    /// <summary>Product whose sole producer-recipe (<see cref="SoleProducerRecipeId"/>) has a photo.</summary>
    public static readonly Guid PhotoInheritingProductId = Guid.CreateVersion7();
    /// <summary>Product with no entry in the fake's photo-inheritance dictionary (zero/many producers
    /// or a photo-less sole producer all collapse to "absent" — this fixture only needs one case).</summary>
    public static readonly Guid NoInheritanceProductId = Guid.CreateVersion7();
    public static readonly Guid SoleProducerRecipeId = Guid.CreateVersion7();
    /// <summary>Recipe dish used on the Dinner meal to prove behavior rule 1 (a Recipe-kind dish
    /// always wins the primary-photo slot, even photo-less, over a photo-resolvable product dish).</summary>
    public static readonly Guid ControlRecipeId = Guid.CreateVersion7();
}

// ── Factory ───────────────────────────────────────────────────────────────────

/// <summary>
/// WAF factory wiring three meals (product-only-with-photo, product-only-without-photo, and
/// recipe+product mixed) plus an <see cref="IRecipeReadModel"/> fake that resolves
/// <see cref="ProductPhotoFixture.PhotoInheritingProductId"/> to <see cref="ProductPhotoFixture.SoleProducerRecipeId"/>
/// and nothing else. Mirrors <c>ProductUnitLabelFactory</c>'s (plantry-ri26) service wiring.
/// </summary>
public sealed class MealCardProductPhotoFactory : MealPlanFragmentFactory
{
    public ProductPhotoMealPlanRepo Repo { get; } = new();

    protected override string FakeUserId => "00000000-0000-0000-0000-0000000000ee";
    protected override IMealPlanRepository MealPlanRepo => Repo;
    protected override IMealSlotConfigRepository SlotConfigRepo => new FakeSlotRepo(ProductPhotoFixture.SlotConfig);
    protected override IHouseholdMemberReader MemberReader => new FakeMemberReader([]);

    // The port under test: resolves ONLY PhotoInheritingProductId → SoleProducerRecipeId.
    protected override IRecipeReadModel RecipeReadModel => new YieldPhotoRecipeReader(
        new Dictionary<Guid, Guid>
        {
            [ProductPhotoFixture.PhotoInheritingProductId] = ProductPhotoFixture.SoleProducerRecipeId,
        });

    protected override IMealPlanCatalogProductReader CatalogProductReader => new ProductPhotoCatalogReader();
}

// ── Plan repo ─────────────────────────────────────────────────────────────────

/// <summary>
/// Meal plan repo for the plantry-f4dt product-dish photo-inheritance scenario. Breakfast and Lunch
/// are product-only meals (no Recipe-kind dish); Dinner mixes a Recipe-kind dish with a product dish
/// that would otherwise resolve a photo, to prove the Recipe-kind dish still wins the primary slot.
/// </summary>
public sealed class ProductPhotoMealPlanRepo : IMealPlanRepository
{
    public Task<IReadOnlyDictionary<Guid, PlannedMealSlotInfo>> FindSlotLabelsAsync(
        IReadOnlyList<Guid> plannedMealIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, PlannedMealSlotInfo>>(new Dictionary<Guid, PlannedMealSlotInfo>());

    private static readonly IClock _clock = new FixedClock(MealPlanningTestClock.Instant);

    public MealPlan ThisWeekPlan { get; }
    public DateOnly ThisWeekMonday { get; }

    public ProductPhotoMealPlanRepo()
    {
        var hhId = SharedKernel.HouseholdId.From(ProductPhotoFixture.HouseholdId);
        var today = DateOnly.FromDateTime(MealPlanningTestClock.Instant.UtcDateTime);
        ThisWeekMonday = MealPlan.NormalizeToMonday(today);

        ThisWeekPlan = MealPlan.Start(hhId, ThisWeekMonday, _clock);

        // Breakfast: product-only meal, sole dish is the photo-inheriting product.
        ThisWeekPlan.AssignMeal(ThisWeekMonday, ProductPhotoFixture.BreakfastSlotId,
            [DishSpec.ForProduct(ProductPhotoFixture.PhotoInheritingProductId, 2m, Guid.NewGuid())],
            null, "manual", Guid.Empty, _clock);

        // Lunch: product-only meal, sole dish has no inherited photo.
        ThisWeekPlan.AssignMeal(ThisWeekMonday, ProductPhotoFixture.LunchSlotId,
            [DishSpec.ForProduct(ProductPhotoFixture.NoInheritanceProductId, 1m, Guid.NewGuid())],
            null, "manual", Guid.Empty, _clock);

        // Dinner: a Recipe-kind dish (no photo, absent from the bag) PLUS the photo-inheriting
        // product dish — the recipe dish must still be picked as primary (behavior rule 1).
        ThisWeekPlan.AssignMeal(ThisWeekMonday, ProductPhotoFixture.DinnerSlotId,
            [
                new DishSpec(DishKind.Recipe, ProductPhotoFixture.ControlRecipeId, 2),
                DishSpec.ForProduct(ProductPhotoFixture.PhotoInheritingProductId, 1m, Guid.NewGuid()),
            ],
            null, "manual", Guid.Empty, _clock);

        // Tuesday Breakfast: product-only meal whose FIRST product dish resolves no photo but whose
        // SECOND one would (behavior rule 2 — "if the first product dish doesn't resolve one, do NOT
        // check later product dishes in the same meal"). A different date/slot pair from the three
        // meals above so it lands in its own grid cell.
        ThisWeekPlan.AssignMeal(ThisWeekMonday.AddDays(1), ProductPhotoFixture.BreakfastSlotId,
            [
                DishSpec.ForProduct(ProductPhotoFixture.NoInheritanceProductId, 1m, Guid.NewGuid()),
                DishSpec.ForProduct(ProductPhotoFixture.PhotoInheritingProductId, 1m, Guid.NewGuid()),
            ],
            null, "manual", Guid.Empty, _clock);
    }

    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
        => Task.FromResult<MealPlan?>(weekStart == ThisWeekMonday ? ThisWeekPlan : null);

    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default)
        => Task.FromResult(ThisWeekPlan);

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

// ── Test doubles ──────────────────────────────────────────────────────────────

/// <summary>
/// <see cref="IRecipeReadModel"/> fake for the plantry-f4dt scenario: every method other than
/// <see cref="FindSoleYieldPhotoRecipeIdsAsync"/> returns "no data" (this suite's plan carries no
/// ghost dishes and no unfulfillability checks that would otherwise need them) — only the photo
/// lookup dictionary is exercised, resolving to whatever the test wires in.
/// </summary>
internal sealed class YieldPhotoRecipeReader(IReadOnlyDictionary<Guid, Guid> soleYieldPhotoRecipeIds) : IRecipeReadModel
{
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
        IReadOnlyCollection<Guid> productIds, CancellationToken ct = default) =>
        Task.FromResult(soleYieldPhotoRecipeIds);
}

/// <summary>
/// Catalog reader for the plantry-f4dt scenario: names the photo-inheriting product "Leftover
/// Lasagna" (so the rendered alt text is distinguishable) and the no-inheritance product "Plain
/// Product"; unit code is irrelevant to this suite so it is fixed at "ea" for both.
/// </summary>
internal sealed class ProductPhotoCatalogReader : IMealPlanCatalogProductReader
{
    public Task<bool> ExistsAsync(Guid productId, CancellationToken ct = default) => Task.FromResult(true);

    public Task<bool> IsPlannableAsync(Guid productId, CancellationToken ct = default) => Task.FromResult(true);

    public Task<IReadOnlyList<MealPlanProductReadModel>> SearchAsync(
        string nameQuery, int maxResults = 20, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MealPlanProductReadModel>>([]);

    public Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            productIds.ToDictionary(
                id => id,
                id => id == ProductPhotoFixture.PhotoInheritingProductId ? "Leftover Lasagna" : "Plain Product"));

    public Task<IReadOnlyDictionary<Guid, string>> ResolveDefaultUnitCodesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, string>>(productIds.ToDictionary(id => id, _ => "ea"));
}
