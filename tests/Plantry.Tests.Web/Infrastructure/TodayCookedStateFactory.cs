using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Identity.Domain;
using Plantry.Intake.Domain;
using Plantry.Pantry.Application;
using Plantry.Pantry.Domain;
using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// L4 WebApplicationFactory for the Today planned-meals band cooked/eaten CTA gate (plantry-ohmb).
/// Boots the full <c>Plantry.Web</c> pipeline with all Postgres-backed seams replaced by in-memory
/// fakes, seeding three of today's default slots so every acceptance criterion is exercised on a
/// single page load:
/// <list type="bullet">
///   <item>Breakfast: single recipe dish, every dish cooked -> AC1 (done indicator, no Cook link,
///     "Already cooked today" hint).</item>
///   <item>Lunch: recipe dish + product dish, only the recipe dish cooked -> AC2 (Cook CTA still
///     shown; a partially-cooked meal is not "cooked").</item>
///   <item>Dinner: single product dish, net-consumed (eaten) -> AC7 (product-dish presence via the
///     port's netting counts as cooked, no recipe-only special-casing).</item>
/// </list>
/// <see cref="CookStatusReader"/> counts <see cref="IMealPlanCookStatusReader.GetStatusesAsync"/>
/// calls so AC6 (exactly one call per page load) is asserted directly.
/// </summary>
public sealed class TodayCookedStateFactory : WebApplicationFactory<Program>
{
    public TodayCountingCookStatusReader CookStatusReader { get; } =
        new(TodayCookedStateFixture.BuildCookedStatuses(TodayCookedStateFixture.SharedPlan));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.AddFakeExpiringSoonHorizon();

            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(TodayCookedStateFixture.Clock);

            services.RemoveAll<IHouseholdRepository>();
            services.AddSingleton<IHouseholdRepository>(new FakeTodayHouseholdRepository());

            services.RemoveAll<IProductStockRepository>();
            services.AddSingleton<IProductStockRepository>(new FakeTodayStockRepository(hasStock: true));

            services.RemoveAll<ICatalogReadFacade>();
            services.AddSingleton<ICatalogReadFacade>(new FakeTodayCatalogReadFacade());

            services.RemoveAll<IProductConversionProvider>();
            services.AddSingleton<IProductConversionProvider>(new FakeTodayConversionProvider());

            services.RemoveAll<IImportSessionRepository>();
            services.AddSingleton<IImportSessionRepository>(new FakeTodaySessionRepository());

            services.RemoveAll<IRecipeRepository>();
            services.AddSingleton<IRecipeRepository>(new FakeTodayPlannedBandRecipeRepository());

            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(new FakeBrowseTagRepository([]));

            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeBrowseCatalogProductReader(new Dictionary<Guid, CatalogProduct>()));

            services.RemoveAll<IInventoryStockReader>();
            services.AddSingleton<IInventoryStockReader>(
                new FakeBrowseStockReader(new Dictionary<Guid, Plantry.Recipes.Application.ProductStock>()));

            services.RemoveAll<IPriceReader>();
            services.AddSingleton<IPriceReader>(new FakeBrowsePriceReader(new Dictionary<Guid, PricePoint>()));

            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeBrowseUnitConverter());

            services.RemoveAll<ICatalogWriter>();
            services.AddSingleton<ICatalogWriter>(new FakeCatalogWriter());

            services.RemoveAll<IMealSlotConfigRepository>();
            services.AddSingleton<IMealSlotConfigRepository>(new TodayCookedStateSlotConfigRepo());

            services.RemoveAll<IMealPlanRepository>();
            services.AddSingleton<IMealPlanRepository>(new TodayCookedStateMealPlanRepo());

            services.RemoveAll<IRecipeReadModel>();
            services.AddSingleton<IRecipeReadModel>(new TodayCookedStateRecipeReadModel());

            services.RemoveAll<IMealPlanStockReader>();
            services.AddSingleton<IMealPlanStockReader>(new FakeTodayNullStockReader());

            services.RemoveAll<IMealPlanCatalogProductReader>();
            services.AddSingleton<IMealPlanCatalogProductReader>(new TodayCookedStateCatalogProductReader());

            // The port under test (plantry-ohmb) — a counting double so AC6 is asserted directly.
            services.RemoveAll<IMealPlanCookStatusReader>();
            services.AddSingleton<IMealPlanCookStatusReader>(CookStatusReader);

            services.RemoveAll<Plantry.Planning.Application.IHouseholdMemberReader>();
            services.AddSingleton<Plantry.Planning.Application.IHouseholdMemberReader>(new FakeTodayPlannedBandMemberReader());

            TodayDealsStubs.RegisterEmpty(services);
        });
    }
}

// ── Fixture data ─────────────────────────────────────────────────────────────────

public static class TodayCookedStateFixture
{
    public static readonly Guid HouseholdId = Guid.Parse("aa000006-0000-0000-0000-000000000006");
    private static readonly HouseholdId HhId = Plantry.SharedKernel.HouseholdId.From(HouseholdId);

    public static readonly Guid RecipeId = Guid.Parse("bb000006-0000-0000-0000-000000000006");
    public static readonly Guid LunchProductId = Guid.Parse("cc000006-0000-0000-0000-000000000006");
    public static readonly Guid LunchUnitId = Guid.Parse("dd000006-0000-0000-0000-000000000006");
    public static readonly Guid DinnerProductId = Guid.Parse("ee000006-0000-0000-0000-000000000006");
    public static readonly Guid DinnerUnitId = Guid.Parse("ff000006-0000-0000-0000-000000000006");

    public static readonly IClock Clock = new SnapshotFixedClock(new DateOnly(2026, 6, 15));

    public static readonly MealSlotConfig SharedSlotConfig =
        MealSlotConfig.CreateWithDefaults(HhId, Clock);

    /// <summary>
    /// Single shared plan instance (built once) — the meal-plan repo double and the cook-status
    /// reader double must observe the SAME plan-assigned <c>PlannedDish</c> ids (generated fresh by
    /// every <see cref="BuildPlan"/> call), so both read from this one instance rather than each
    /// building their own (which would desync the ids the page renders from the ids the cook-status
    /// dictionary is keyed by).
    /// </summary>
    public static readonly MealPlan SharedPlan = BuildPlan(SharedSlotConfig);

    /// <summary>
    /// Builds today's plan: Breakfast = single recipe dish (fully cooked); Lunch = recipe dish
    /// (ordinal 0, cooked) + product dish (ordinal 1, NOT cooked — the meal stays partially cooked,
    /// AC2); Dinner = single product dish (net-consumed, AC7).
    /// </summary>
    public static MealPlan BuildPlan(MealSlotConfig slotConfig)
    {
        var today = Clock.ToLocalDate(Clock.UtcNow);
        var plan = MealPlan.Start(HhId, today, Clock);

        var ordered = slotConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).ToList();
        var breakfast = ordered[0];
        var lunch = ordered[1];
        var dinner = ordered[2];

        plan.AssignMeal(
            today, breakfast.Id,
            [new DishSpec(DishKind.Recipe, RecipeId, 2)],
            attendeesOverride: null, source: "test", createdBy: Guid.Empty, Clock);

        plan.AssignMeal(
            today, lunch.Id,
            [
                new DishSpec(DishKind.Recipe, RecipeId, 2),
                DishSpec.ForProduct(LunchProductId, 1m, LunchUnitId),
            ],
            attendeesOverride: null, source: "test", createdBy: Guid.Empty, Clock);

        plan.AssignMeal(
            today, dinner.Id,
            [DishSpec.ForProduct(DinnerProductId, 2m, DinnerUnitId)],
            attendeesOverride: null, source: "test", createdBy: Guid.Empty, Clock);

        return plan;
    }

    /// <summary>
    /// Derives the cook-status dictionary from the ACTUAL plan-assigned <c>PlannedDish</c> ids
    /// (generated internally by <see cref="MealPlan.AssignMeal"/>, not known up front): Breakfast's
    /// one dish, Lunch's first (recipe) dish only, and Dinner's one dish — Lunch's second (product)
    /// dish is deliberately absent so the meal stays partially cooked (AC2).
    /// </summary>
    public static IReadOnlyDictionary<Guid, DishCookStatus> BuildCookedStatuses(MealPlan plan)
    {
        var today = Clock.ToLocalDate(Clock.UtcNow);
        var ordered = SharedSlotConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).ToList();
        var breakfastMeal = plan.MealsInCell(today, ordered[0].Id).Single();
        var lunchMeal = plan.MealsInCell(today, ordered[1].Id).Single();
        var dinnerMeal = plan.MealsInCell(today, ordered[2].Id).Single();

        var lunchFirstDishId = lunchMeal.PlannedDishes.OrderBy(d => d.Ordinal).First().Id.Value;

        var now = Clock.UtcNow;
        var result = new Dictionary<Guid, DishCookStatus>
        {
            [breakfastMeal.PlannedDishes.Single().Id.Value] = new DishCookStatus(now),
            [lunchFirstDishId] = new DishCookStatus(now),
            [dinnerMeal.PlannedDishes.Single().Id.Value] = new DishCookStatus(now, ConsumedQuantity: 2m, ConsumedUnitId: DinnerUnitId),
        };
        return result;
    }
}

// ── Test doubles ──────────────────────────────────────────────────────────────

internal sealed class TodayCookedStateSlotConfigRepo : IMealSlotConfigRepository
{
    private readonly MealSlotConfig _config = TodayCookedStateFixture.SharedSlotConfig;

    public Task<MealSlotConfig?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        => Task.FromResult<MealSlotConfig?>(_config);
    public Task AddAsync(MealSlotConfig config, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class TodayCookedStateMealPlanRepo : IMealPlanRepository
{
    public Task<IReadOnlyDictionary<Guid, PlannedMealSlotInfo>> FindSlotLabelsAsync(
        IReadOnlyList<Guid> plannedMealIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, PlannedMealSlotInfo>>(new Dictionary<Guid, PlannedMealSlotInfo>());

    private readonly MealPlan _plan = TodayCookedStateFixture.SharedPlan;

    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
        => Task.FromResult<MealPlan?>(_plan);
    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default)
        => Task.FromResult(_plan);
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Recipe read model resolving the single fixture recipe, 100% fulfillable (ready hint baseline).</summary>
internal sealed class TodayCookedStateRecipeReadModel : IRecipeReadModel
{
    private static readonly Guid RecipeId = TodayCookedStateFixture.RecipeId;
    private static readonly RecipeReadModel FixtureModel =
        new(RecipeId, "Pasta Carbonara", [], DefaultServings: 2, HasPhoto: false, CookTimeMinutes: 20);
    private static readonly RecipeDishEnrichment FullyReadyEnrichment =
        new(FulfillmentPercent: 100, TotalCost: null, CostIsPartial: false, HasExpiringIngredients: false);

    public Task<RecipeReadModel?> GetByIdAsync(Guid recipeId, CancellationToken ct = default)
        => Task.FromResult<RecipeReadModel?>(recipeId == RecipeId ? FixtureModel : null);
    public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string nameQuery, int maxResults = 20, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeReadModel>>([FixtureModel]);
    public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid recipeId, int servings, DateOnly today, CancellationToken ct = default)
        => Task.FromResult<RecipeDishEnrichment?>(recipeId == RecipeId ? FullyReadyEnrichment : null);
    public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid recipeId, int servings, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeMissingIngredient>>([]);
    public Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default)
        => Task.FromResult(false);
}

/// <summary>Catalog-product reader resolving the Lunch and Dinner product dishes' names/units.</summary>
internal sealed class TodayCookedStateCatalogProductReader : IMealPlanCatalogProductReader
{
    public Task<bool> ExistsAsync(Guid productId, CancellationToken ct = default) => Task.FromResult(true);
    public Task<bool> IsPlannableAsync(Guid productId, CancellationToken ct = default) => Task.FromResult(true);
    public Task<IReadOnlyList<MealPlanProductReadModel>> SearchAsync(
        string nameQuery, int maxResults = 20, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MealPlanProductReadModel>>([]);

    public Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        var names = new Dictionary<Guid, string>
        {
            [TodayCookedStateFixture.LunchProductId] = "Rice",
            [TodayCookedStateFixture.DinnerProductId] = "Leftovers",
        };
        return Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            productIds.Where(names.ContainsKey).ToDictionary(id => id, id => names[id]));
    }

    public Task<IReadOnlyDictionary<Guid, string>> ResolveDefaultUnitCodesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        var units = new Dictionary<Guid, string>
        {
            [TodayCookedStateFixture.LunchProductId] = "ea",
            [TodayCookedStateFixture.DinnerProductId] = "ea",
        };
        return Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            productIds.Where(units.ContainsKey).ToDictionary(id => id, id => units[id]));
    }

    public Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
        IReadOnlyCollection<Guid> unitIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            unitIds.ToDictionary(id => id, _ => "ea"));
}

/// <summary>
/// Cook/eaten status reader (plantry-ohmb) that counts <see cref="GetStatusesAsync"/> calls (AC6)
/// and returns the fixture's cooked-dish dictionary, keyed against <see cref="TodayCookedStateFixture.SharedPlan"/>
/// — the SAME plan instance <see cref="TodayCookedStateMealPlanRepo"/> serves, so the dish ids agree.
/// </summary>
public sealed class TodayCountingCookStatusReader(IReadOnlyDictionary<Guid, DishCookStatus> statuses)
    : IMealPlanCookStatusReader
{
    public int GetStatusesAsyncCallCount { get; private set; }

    public Task<IReadOnlyDictionary<Guid, DishCookStatus>> GetStatusesAsync(
        IReadOnlyCollection<Guid> plannedDishIds, CancellationToken ct = default)
    {
        GetStatusesAsyncCallCount++;
        IReadOnlyDictionary<Guid, DishCookStatus> result = plannedDishIds
            .Where(statuses.ContainsKey)
            .ToDictionary(id => id, id => statuses[id]);
        return Task.FromResult(result);
    }
}
