using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Identity.Domain;
using Plantry.Intake.Domain;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// L4 WebApplicationFactory for the Today page planned-meals band (plantry-zp7) — planned state.
/// Boots the full <c>Plantry.Web</c> pipeline with all Postgres-backed seams replaced by in-memory fakes.
///
/// Fixture scenario:
/// <list type="bullet">
///   <item>Household has stock (hasStock=true → IsColdStart=false → board renders).</item>
///   <item>Slot config: default 3 slots (Breakfast, Lunch, Dinner) from <c>MealSlotConfig.CreateWithDefaults</c>.</item>
///   <item>Meal plan: one meal in the Breakfast slot for today — a recipe dish (PastaCarbonara, RecipeId = <see cref="TodayPlannedBandFixture.RecipeId"/>).</item>
///   <item>Recipe read model: returns display info (name="Pasta Carbonara", HasPhoto=true) for the fixture recipe.</item>
///   <item>Recipe repository: returns a recipe with CookTimeMinutes=20 for <c>GetByIdAsync</c>.</item>
///   <item>Fulfillment: 100% (all in stock, hasExpiring=false) → ready hint shown.</item>
///   <item>Lunch + Dinner slots: empty → "Nothing planned yet" affordance.</item>
/// </list>
/// </summary>
public sealed class TodayPlannedMealsBandFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // ── Auth ─────────────────────────────────────────────────────────
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // ── Identity ──────────────────────────────────────────────────────
            services.RemoveAll<IHouseholdRepository>();
            services.AddSingleton<IHouseholdRepository>(new FakeTodayHouseholdRepository());

            // ── Inventory ─────────────────────────────────────────────────────
            // hasStock=true → IsColdStart=false → board renders
            services.RemoveAll<IProductStockRepository>();
            services.AddSingleton<IProductStockRepository>(new FakeTodayStockRepository(hasStock: true));

            services.RemoveAll<ICatalogReadFacade>();
            services.AddSingleton<ICatalogReadFacade>(new FakeTodayCatalogReadFacade());

            services.RemoveAll<IProductConversionProvider>();
            services.AddSingleton<IProductConversionProvider>(new FakeTodayConversionProvider());

            // ── Intake — no pending sessions ─────────────────────────────────
            services.RemoveAll<IImportSessionRepository>();
            services.AddSingleton<IImportSessionRepository>(new FakeTodaySessionRepository());

            // ── Recipes (cross-context seams; still needed by BrowseRecipesQuery on /Recipes page) ─
            services.RemoveAll<IRecipeRepository>();
            services.AddSingleton<IRecipeRepository>(
                new FakeTodayPlannedBandRecipeRepository());

            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(new FakeBrowseTagRepository([]));

            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeBrowseCatalogProductReader(new Dictionary<Guid, CatalogProduct>()));

            services.RemoveAll<IInventoryStockReader>();
            services.AddSingleton<IInventoryStockReader>(
                new FakeBrowseStockReader(new Dictionary<Guid, Plantry.Recipes.Application.ProductStock>()));

            services.RemoveAll<IPriceReader>();
            services.AddSingleton<IPriceReader>(
                new FakeBrowsePriceReader(new Dictionary<Guid, PricePoint>()));

            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeBrowseUnitConverter());

            services.RemoveAll<ICatalogWriter>();
            services.AddSingleton<ICatalogWriter>(new FakeCatalogWriter());

            // ── MealPlanning seams (plantry-zp7) ─────────────────────────────
            services.RemoveAll<IMealSlotConfigRepository>();
            services.AddSingleton<IMealSlotConfigRepository>(
                new FakeTodayPlannedBandSlotConfigRepo());

            services.RemoveAll<IMealPlanRepository>();
            services.AddSingleton<IMealPlanRepository>(
                new FakeTodayPlannedBandMealPlanRepo());

            services.RemoveAll<IRecipeReadModel>();
            services.AddSingleton<IRecipeReadModel>(
                new FakeTodayPlannedBandRecipeReadModel());

            services.RemoveAll<IMealPlanStockReader>();
            services.AddSingleton<IMealPlanStockReader>(new FakeTodayNullStockReader());

            // IHouseholdMemberReader — Today page now loads members for attendee avatars.
            // The fixture household has one member (the registering user — Guid.Empty for simplicity).
            services.RemoveAll<IHouseholdMemberReader>();
            services.AddSingleton<IHouseholdMemberReader>(new FakeTodayPlannedBandMemberReader());

            // Empty Deals seams (plantry-bpw) — Today now consumes BrowseDeals for the deal banner.
            TodayDealsStubs.RegisterEmpty(services);
        });
    }
}

/// <summary>
/// L4 WebApplicationFactory for the Today page planned-meals band (plantry-zp7) — no-slots state.
/// Same as <see cref="TodayPlannedMealsBandFactory"/> but the slot config repo returns null,
/// so no active slots are loaded → the empty-slots prompt ("No meal slots set up") renders.
/// </summary>
public sealed class TodayPlannedMealsBandNoSlotsFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

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
            services.AddSingleton<IRecipeRepository>(
                new FakeTodayPlannedBandRecipeRepository());

            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(new FakeBrowseTagRepository([]));

            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeBrowseCatalogProductReader(new Dictionary<Guid, CatalogProduct>()));

            services.RemoveAll<IInventoryStockReader>();
            services.AddSingleton<IInventoryStockReader>(
                new FakeBrowseStockReader(new Dictionary<Guid, Plantry.Recipes.Application.ProductStock>()));

            services.RemoveAll<IPriceReader>();
            services.AddSingleton<IPriceReader>(
                new FakeBrowsePriceReader(new Dictionary<Guid, PricePoint>()));

            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeBrowseUnitConverter());

            services.RemoveAll<ICatalogWriter>();
            services.AddSingleton<ICatalogWriter>(new FakeCatalogWriter());

            // No slot config → empty-slots prompt
            TodayMealPlanningStubs.RegisterNull(services);

            // Empty Deals seams (plantry-bpw) — Today now consumes BrowseDeals for the deal banner.
            TodayDealsStubs.RegisterEmpty(services);
        });
    }
}

// ── Fixture data ─────────────────────────────────────────────────────────────────

/// <summary>
/// Stable identifiers and data for the planned-meals-band L4 fragment fixture (plantry-zp7).
/// The slot config is a shared singleton so that <see cref="FakeTodayPlannedBandSlotConfigRepo"/>
/// and <see cref="FakeTodayPlannedBandMealPlanRepo"/> use the same <c>MealSlotId</c> values.
/// <c>MealSlotConfig.CreateWithDefaults</c> generates new GUIDs on every call, so callers
/// must not call <see cref="BuildSlotConfig"/> more than once — use <see cref="SharedSlotConfig"/>.
/// </summary>
public static class TodayPlannedBandFixture
{
    public static readonly Guid HouseholdId = Guid.Parse("aa000001-0000-0000-0000-000000000001");
    private static readonly HouseholdId HhId = Plantry.SharedKernel.HouseholdId.From(HouseholdId);
    public static readonly Guid RecipeId = Guid.Parse("bb000001-0000-0000-0000-000000000001");
    private static readonly IClock Clock = Plantry.SharedKernel.Domain.SystemClock.Instance;

    /// <summary>
    /// Shared singleton slot config — both the slot-config repo and the meal-plan repo must
    /// return slots built from this single instance to ensure their <c>MealSlotId</c> values match.
    /// </summary>
    public static readonly MealSlotConfig SharedSlotConfig =
        MealSlotConfig.CreateWithDefaults(HhId, Clock);

    /// <summary>
    /// Kept for unit-test use; do NOT call this more than once in a single factory — use
    /// <see cref="SharedSlotConfig"/> instead so all repos share the same slot IDs.
    /// </summary>
    public static MealSlotConfig BuildSlotConfig() =>
        MealSlotConfig.CreateWithDefaults(HhId, Clock);

    /// <summary>
    /// Builds a <see cref="MealPlan"/> for the current week with one meal in the Breakfast slot
    /// for today, containing a recipe dish (RecipeId = <see cref="RecipeId"/>).
    /// </summary>
    public static MealPlan BuildPlanWithBreakfast(MealSlotConfig slotConfig)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var plan = MealPlan.Start(HhId, today, Clock);

        // Breakfast slot = first active slot by ordinal
        var breakfast = slotConfig.Slots
            .Where(s => s.IsActive)
            .OrderBy(s => s.Ordinal)
            .First();

        plan.AssignMeal(
            today,
            breakfast.Id,
            [new DishSpec(DishKind.Recipe, RecipeId, 2)],
            attendeesOverride: null,
            source: "test",
            createdBy: Guid.Empty,
            Clock);

        return plan;
    }
}

// ── Test doubles ──────────────────────────────────────────────────────────────

/// <summary>
/// Slot config repo that returns the fixture default slots (Breakfast, Lunch, Dinner).
/// Uses <see cref="TodayPlannedBandFixture.SharedSlotConfig"/> so slot IDs match the meal plan.
/// </summary>
internal sealed class FakeTodayPlannedBandSlotConfigRepo : IMealSlotConfigRepository
{
    private readonly MealSlotConfig _config = TodayPlannedBandFixture.SharedSlotConfig;

    public Task<MealSlotConfig?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        => Task.FromResult<MealSlotConfig?>(_config);

    public Task AddAsync(MealSlotConfig config, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Meal plan repo that returns the fixture plan (one meal: Breakfast, recipe dish).
/// Seeded using <see cref="TodayPlannedBandFixture.SharedSlotConfig"/> so the breakfast slot ID
/// matches what <see cref="FakeTodayPlannedBandSlotConfigRepo"/> returns.
/// </summary>
internal sealed class FakeTodayPlannedBandMealPlanRepo : IMealPlanRepository
{
    private readonly MealPlan _plan;

    public FakeTodayPlannedBandMealPlanRepo()
    {
        // Use the same slot config instance as the slot-config repo: slot IDs must match
        // so IndexModel's plan.MealsInCell(today, slot.Id) finds the breakfast meal.
        _plan = TodayPlannedBandFixture.BuildPlanWithBreakfast(TodayPlannedBandFixture.SharedSlotConfig);
    }

    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
        => Task.FromResult<MealPlan?>(_plan);

    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default)
        => Task.FromResult(_plan);

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Recipe read model that returns display info for the fixture recipe.
/// HasPhoto=true so the photo img renders (not the placeholder).
/// GetEnrichmentAsync returns 100% fulfillment, no expiring → ready hint.
/// </summary>
internal sealed class FakeTodayPlannedBandRecipeReadModel : IRecipeReadModel
{
    private static readonly Guid RecipeId = TodayPlannedBandFixture.RecipeId;
    private static readonly RecipeReadModel FixtureModel =
        new(RecipeId, "Pasta Carbonara", [], DefaultServings: 2, HasPhoto: true);
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

/// <summary>
/// Recipe repository that returns a fixture recipe with CookTimeMinutes=20 for <see cref="GetByIdAsync"/>.
/// AnyForHouseholdAsync returns true (has recipes → not cold start).
/// </summary>
internal sealed class FakeTodayPlannedBandRecipeRepository : IRecipeRepository
{
    private static readonly IClock Clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
    private static readonly Guid HouseholdGuid = TodayPlannedBandFixture.HouseholdId;
    private static readonly HouseholdId HhId = Plantry.SharedKernel.HouseholdId.From(HouseholdGuid);
    private static readonly Guid RecipeGuid = TodayPlannedBandFixture.RecipeId;

    private readonly Recipe _recipe;

    public FakeTodayPlannedBandRecipeRepository()
    {
        var result = Recipe.Create(HhId, "Pasta Carbonara", defaultServings: 2, Clock);
        _recipe = result.Value;
        _recipe.SetCookTime(20, Clock);
    }

    public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<Recipe?> GetByIdAsync(RecipeId id, CancellationToken ct = default)
    {
        // Return the fixture recipe for any ID (the exact ID from the plan may differ due to Recipe.Create generating its own ID)
        return Task.FromResult<Recipe?>(_recipe);
    }

    public Task<IReadOnlyList<Recipe>> ListForBrowseAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Recipe>>([_recipe]);

    public Task<IReadOnlySet<RecipeId>> ListRecipeIdsWithPhotoAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<RecipeId>>(new HashSet<RecipeId>());

    public Task AddAsync(Recipe recipe, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> NameExistsAsync(HouseholdId householdId, string name, CancellationToken ct = default) => Task.FromResult(false);
    public Task<IReadOnlyDictionary<RecipeId, string>> GetRecipeNamesByIdAsync(
        IReadOnlyList<RecipeId> ids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<RecipeId, string>>(new Dictionary<RecipeId, string>());
}

/// <summary>
/// Null stock reader — no product dishes in the fixture, so stock is never queried.
/// </summary>
internal sealed class FakeTodayNullStockReader : IMealPlanStockReader
{
    public Task<MealPlanProductStock?> FindStockAsync(Guid productId, CancellationToken ct = default)
        => Task.FromResult<MealPlanProductStock?>(null);
}

/// <summary>
/// Household member reader that returns an empty list for the planned-band fixture.
/// The fixture plan uses attendeesOverride: null (slot defaults), and the slot defaults
/// are also empty, so no attendee avatars render — the Today band shows slots without
/// the attendees section. Inject real members here if an attendee-avatar test is added.
/// </summary>
internal sealed class FakeTodayPlannedBandMemberReader : IHouseholdMemberReader
{
    public Task<IReadOnlyList<HouseholdMember>> ListMembersAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<HouseholdMember>>([]);
}
