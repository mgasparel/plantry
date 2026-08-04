using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Today;

/// <summary>
/// L4 regression coverage for plantry-r2yf: the Today band's recipe-dish resolution (name,
/// HasPhoto, CookTimeMinutes) must be one batched <see cref="IRecipeReadModel.GetByIdsAsync"/> call
/// for the whole page load — never a per-dish <see cref="IRecipeReadModel.GetByIdAsync"/> round trip,
/// and never any <see cref="IRecipeRepository.GetByIdAsync"/> call at all (the composition-root
/// repository hop this ticket removed) — mirroring plantry-nlg4's product-resolution batching
/// regression suite, <see cref="TodayProductResolutionBatchingTests"/>.
/// </summary>
public sealed class TodayRecipeResolutionBatchingTests
{
    [Fact(DisplayName = "GET /Today: recipe dishes across slots resolve via exactly one batch call, and a recipe planned in two slots resolves once (AC1, AC2)")]
    public async Task RecipeDishesAcrossSlots_ResolveInOneBatchCall_AndDedupeRepeatedRecipe()
    {
        await using var factory = new TodayRecipeBatchingFactory(TodayRecipeBatchingFixture.BuildPlanWithRepeatedRecipe());
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, TodayRecipeBatchingFixture.HouseholdId.ToString());

        var response = await client.GetAsync("/Today");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // AC1: the read-model lookup is issued at most once per page load, regardless of how many
        // slots/dishes the day contains — and never a per-dish GetByIdAsync round trip.
        Assert.Equal(1, factory.RecipeReadModel.GetByIdsAsyncCallCount);
        Assert.Equal(0, factory.RecipeReadModel.GetByIdAsyncCallCount);

        // AC1 (tightened by the design decision in plantry-r2yf's notes): zero Recipes-repository
        // calls — the CookTimeMinutes hop this ticket removed entirely.
        Assert.Equal(0, factory.RecipeRepo.GetByIdAsyncCallCount);

        // AC2: the same recipe (Recipe Alpha) planned in two different slots (Breakfast, Lunch)
        // resolves once and renders correctly in both.
        Assert.Equal(2, CountOccurrences(html, "Recipe Alpha"));
        Assert.Contains("Recipe Beta", html);

        // AC3: cook time still renders unchanged for the primary (first-planned) recipe dish.
        Assert.Contains("15m", html);
    }

    [Fact(DisplayName = "GET /Today: a day with zero recipe dishes issues zero recipe-resolution batch calls (AC4)")]
    public async Task ZeroRecipeDishes_PerformsZeroCalls()
    {
        await using var factory = new TodayRecipeBatchingFactory(TodayRecipeBatchingFixture.BuildProductOnlyPlan());
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, TodayRecipeBatchingFixture.HouseholdId.ToString());

        var response = await client.GetAsync("/Today");
        response.EnsureSuccessStatusCode();

        Assert.Equal(0, factory.RecipeReadModel.GetByIdsAsyncCallCount);
        Assert.Equal(0, factory.RecipeReadModel.GetByIdAsyncCallCount);
        Assert.Equal(0, factory.RecipeRepo.GetByIdAsyncCallCount);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}

// ── Fixture ───────────────────────────────────────────────────────────────────

internal static class TodayRecipeBatchingFixture
{
    public static readonly Guid HouseholdId = Guid.Parse("aa000005-0000-0000-0000-000000000005");
    private static readonly HouseholdId HhId = Plantry.SharedKernel.HouseholdId.From(HouseholdId);

    public static readonly IClock Clock = new SnapshotFixedClock(new DateOnly(2026, 6, 15));

    public static readonly MealSlotConfig SlotConfig = MealSlotConfig.CreateWithDefaults(HhId, Clock);

    public static readonly Guid RecipeAlphaId = Guid.CreateVersion7();
    public static readonly Guid RecipeBetaId = Guid.CreateVersion7();
    public static readonly Guid FlourProductId = Guid.CreateVersion7();

    /// <summary>
    /// Breakfast = Recipe Alpha, Lunch = Recipe Alpha again (AC2: same recipe, two slots),
    /// Dinner = Recipe Beta. Breakfast is the first-planned dish, so Recipe Alpha's cook time
    /// (15m) is the one that must render as the primary recipe's cook time (AC3).
    /// </summary>
    public static MealPlan BuildPlanWithRepeatedRecipe()
    {
        var today = Clock.ToLocalDate(Clock.UtcNow);
        var plan = MealPlan.Start(HhId, today, Clock);
        var ordered = SlotConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).ToList();

        plan.AssignMeal(today, ordered[0].Id, [new DishSpec(DishKind.Recipe, RecipeAlphaId, 2)],
            null, "test", Guid.Empty, Clock);
        plan.AssignMeal(today, ordered[1].Id, [new DishSpec(DishKind.Recipe, RecipeAlphaId, 3)],
            null, "test", Guid.Empty, Clock);
        plan.AssignMeal(today, ordered[2].Id, [new DishSpec(DishKind.Recipe, RecipeBetaId, 1)],
            null, "test", Guid.Empty, Clock);

        return plan;
    }

    /// <summary>Single product dish, zero recipe dishes — AC4's "zero calls" counterpart.</summary>
    public static MealPlan BuildProductOnlyPlan()
    {
        var today = Clock.ToLocalDate(Clock.UtcNow);
        var plan = MealPlan.Start(HhId, today, Clock);
        var breakfast = SlotConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();

        plan.AssignMeal(today, breakfast.Id, [DishSpec.ForProduct(FlourProductId, 2m, Guid.NewGuid())],
            null, "test", Guid.Empty, Clock);

        return plan;
    }
}

// ── Factory ───────────────────────────────────────────────────────────────────

/// <summary>
/// Reuses <see cref="TodayProductBatchingCommon.ConfigureSeams"/> (plantry-nlg4) — the ~20 seams
/// that never vary across the Today batching-regression suites — supplying only the six that do:
/// this scenario's plan, slot config, clock, a null product-catalog reader (no product dishes),
/// and the two call-counting doubles this file's assertions read.
/// </summary>
public sealed class TodayRecipeBatchingFactory(MealPlan plan) : WebApplicationFactory<Program>
{
    public TodayCountingRecipeReadModel RecipeReadModel { get; } = new();
    public TodayCountingRecipeRepository RecipeRepo { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            TodayProductBatchingCommon.ConfigureSeams(
                services, plan, TodayRecipeBatchingFixture.SlotConfig, TodayRecipeBatchingFixture.Clock,
                new FakeTodayNullCatalogProductReader(), RecipeReadModel, RecipeRepo);
        });
    }
}

/// <summary>
/// Recipe read model that counts calls to <see cref="GetByIdsAsync"/> (the batched entry point
/// Today's <c>LoadPlannedMealsTodayAsync</c> must use) and <see cref="GetByIdAsync"/> (the per-dish
/// entry point it must NOT use) separately, so AC1's "one batch call, never per-dish" claim is
/// asserted directly rather than only inferred from rendered output. Resolves Recipe Alpha (cook
/// time 15m) and Recipe Beta (cook time 30m); any other id is simply absent from the result.
/// </summary>
public sealed class TodayCountingRecipeReadModel : IRecipeReadModel
{
    public int GetByIdsAsyncCallCount { get; private set; }
    public int GetByIdAsyncCallCount { get; private set; }

    private static readonly Dictionary<Guid, RecipeReadModel> Recipes = new()
    {
        [TodayRecipeBatchingFixture.RecipeAlphaId] = new RecipeReadModel(
            TodayRecipeBatchingFixture.RecipeAlphaId, "Recipe Alpha", [], DefaultServings: 2,
            HasPhoto: false, CookTimeMinutes: 15),
        [TodayRecipeBatchingFixture.RecipeBetaId] = new RecipeReadModel(
            TodayRecipeBatchingFixture.RecipeBetaId, "Recipe Beta", [], DefaultServings: 2,
            HasPhoto: false, CookTimeMinutes: 30),
    };

    public Task<RecipeReadModel?> GetByIdAsync(Guid recipeId, CancellationToken ct = default)
    {
        GetByIdAsyncCallCount++;
        return Task.FromResult(Recipes.GetValueOrDefault(recipeId));
    }

    public Task<IReadOnlyDictionary<Guid, RecipeReadModel>> GetByIdsAsync(
        IReadOnlyCollection<Guid> recipeIds, CancellationToken ct = default)
    {
        GetByIdsAsyncCallCount++;
        IReadOnlyDictionary<Guid, RecipeReadModel> result = recipeIds
            .Where(Recipes.ContainsKey)
            .ToDictionary(id => id, id => Recipes[id]);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string nameQuery, int maxResults = 20, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeReadModel>>(Recipes.Values.ToList());

    public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid recipeId, int servings, DateOnly today, CancellationToken ct = default)
        => Task.FromResult<RecipeDishEnrichment?>(
            Recipes.ContainsKey(recipeId)
                ? new RecipeDishEnrichment(FulfillmentPercent: 100, TotalCost: null, CostIsPartial: false, HasExpiringIngredients: false)
                : null);

    public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid recipeId, int servings, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeMissingIngredient>>([]);

    public Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default)
        => Task.FromResult(false);
}

/// <summary>
/// Recipe repository that counts <see cref="GetByIdAsync"/> calls so AC1's "zero repository calls"
/// claim (the composition-root CookTimeMinutes hop this ticket removed) is asserted directly.
/// <see cref="AnyForHouseholdAsync"/> is unrelated to the per-dish N+1 this ticket fixes and is not
/// counted — <c>OnGetAsync</c> calls it once, unconditionally, for the cold-start check.
/// </summary>
public sealed class TodayCountingRecipeRepository : IRecipeRepository
{
    public int GetByIdAsyncCallCount { get; private set; }

    public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<Recipe?> GetByIdAsync(RecipeId id, CancellationToken ct = default)
    {
        GetByIdAsyncCallCount++;
        return Task.FromResult<Recipe?>(null);
    }

    public Task<IReadOnlyList<Recipe>> ListForBrowseAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Recipe>>([]);

    public Task<IReadOnlySet<RecipeId>> ListRecipeIdsWithPhotoAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<RecipeId>>(new HashSet<RecipeId>());

    public Task AddAsync(Recipe recipe, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> NameExistsAsync(HouseholdId householdId, string name, CancellationToken ct = default) => Task.FromResult(false);
    public Task<IReadOnlyDictionary<RecipeId, string>> GetRecipeNamesByIdAsync(
        IReadOnlyList<RecipeId> ids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<RecipeId, string>>(new Dictionary<RecipeId, string>());

    public Task<IReadOnlyList<RecipeInclusionEdge>> ListInclusionEdgesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RecipeInclusionEdge>>([]);

    public Task<IReadOnlySet<RecipeId>> GetIncluderIdsAsync(
        RecipeId subRecipeId, bool transitive = false, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<RecipeId>>(new HashSet<RecipeId>());
}
