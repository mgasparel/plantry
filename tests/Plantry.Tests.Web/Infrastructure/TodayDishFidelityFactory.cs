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
/// L4 WebApplicationFactory for the Today planned-meals band per-dish fidelity (plantry-nlg4).
/// Boots the full <c>Plantry.Web</c> pipeline with all Postgres-backed seams replaced by in-memory fakes.
///
/// Fixture scenario — all three default slots planned today:
/// <list type="bullet">
///   <item>Breakfast: a single <b>product</b> dish — "Chicken thighs", 5, resolves to unit "lb" (AC1).</item>
///   <item>Lunch: a <b>mixed</b> meal — a product dish ("Rice", 3, unit unresolved → "?", AC4) at
///     ordinal 0, and a recipe dish ("Pasta Bake", 2 servings) at ordinal 1 (AC3).</item>
///   <item>Dinner: a single <b>recipe</b> dish, 1 serving — singular pluralisation (AC2).</item>
/// </list>
/// </summary>
public sealed class TodayDishFidelityFactory : WebApplicationFactory<Program>
{
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
            services.AddSingleton<IClock>(TodayDishFidelityFixture.Clock);

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

            // ── MealPlanning seams (plantry-nlg4) ────────────────────────────
            services.RemoveAll<IMealSlotConfigRepository>();
            services.AddSingleton<IMealSlotConfigRepository>(new FixedSlotConfigRepo());

            services.RemoveAll<IMealPlanRepository>();
            services.AddSingleton<IMealPlanRepository>(new FixedMealPlanRepo());

            services.RemoveAll<IRecipeReadModel>();
            services.AddSingleton<IRecipeReadModel>(new FixedRecipeReadModel());

            services.RemoveAll<IMealPlanStockReader>();
            services.AddSingleton<IMealPlanStockReader>(new FakeTodayNullStockReader());

            services.RemoveAll<IMealPlanCatalogProductReader>();
            services.AddSingleton<IMealPlanCatalogProductReader>(new FixedCatalogProductReader());

            // Cook/eaten status port (plantry-ohmb) — this fixture's dishes are never cooked/eaten,
            // so an empty-result null double is enough; IndexModel still requires an instance.
            services.RemoveAll<IMealPlanCookStatusReader>();
            services.AddSingleton<IMealPlanCookStatusReader>(new NullCookStatusReader());

            services.RemoveAll<Plantry.Planning.Application.IHouseholdMemberReader>();
            services.AddSingleton<Plantry.Planning.Application.IHouseholdMemberReader>(new FakeTodayPlannedBandMemberReader());

            TodayDealsStubs.RegisterEmpty(services);
            TodayWasteStatsStubs.RegisterEmpty(services);
        });
    }
}

// ── Fixture data ─────────────────────────────────────────────────────────────────

/// <summary>
/// Stable identifiers/data for the per-dish fidelity L4 fixture (plantry-nlg4).
/// </summary>
public static class TodayDishFidelityFixture
{
    public static readonly Guid HouseholdId = Guid.Parse("aa000002-0000-0000-0000-000000000002");
    private static readonly HouseholdId HhId = Plantry.SharedKernel.HouseholdId.From(HouseholdId);

    public static readonly Guid RecipeId = Guid.Parse("bb000002-0000-0000-0000-000000000002");
    public static readonly Guid ChickenThighsProductId = Guid.Parse("cc000002-0000-0000-0000-000000000002");
    public static readonly Guid RiceProductId = Guid.Parse("dd000002-0000-0000-0000-000000000002");
    public static readonly Guid ChickenUnitId = Guid.Parse("ee000002-0000-0000-0000-000000000002");

    public static readonly IClock Clock = new SnapshotFixedClock(new DateOnly(2026, 6, 15));

    public static readonly MealSlotConfig SharedSlotConfig =
        MealSlotConfig.CreateWithDefaults(HhId, Clock);

    /// <summary>
    /// Builds today's plan: Breakfast = product-only (Chicken thighs, 5); Lunch = mixed
    /// (Rice product at ordinal 0, Pasta Bake recipe at ordinal 1); Dinner = recipe-only, 1 serving.
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
            [DishSpec.ForProduct(ChickenThighsProductId, 5m, ChickenUnitId)],
            attendeesOverride: null, source: "test", createdBy: Guid.Empty, Clock);

        plan.AssignMeal(
            today, lunch.Id,
            [
                DishSpec.ForProduct(RiceProductId, 3m, Guid.NewGuid()),
                new DishSpec(DishKind.Recipe, RecipeId, 2),
            ],
            attendeesOverride: null, source: "test", createdBy: Guid.Empty, Clock);

        plan.AssignMeal(
            today, dinner.Id,
            [new DishSpec(DishKind.Recipe, RecipeId, 1)],
            attendeesOverride: null, source: "test", createdBy: Guid.Empty, Clock);

        return plan;
    }
}

// ── Test doubles ──────────────────────────────────────────────────────────────

internal sealed class FixedSlotConfigRepo : IMealSlotConfigRepository
{
    private readonly MealSlotConfig _config = TodayDishFidelityFixture.SharedSlotConfig;

    public Task<MealSlotConfig?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        => Task.FromResult<MealSlotConfig?>(_config);
    public Task AddAsync(MealSlotConfig config, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FixedMealPlanRepo : IMealPlanRepository
{
    public Task<IReadOnlyDictionary<Guid, PlannedMealSlotInfo>> FindSlotLabelsAsync(
        IReadOnlyList<Guid> plannedMealIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, PlannedMealSlotInfo>>(new Dictionary<Guid, PlannedMealSlotInfo>());

    private readonly MealPlan _plan =
        TodayDishFidelityFixture.BuildPlan(TodayDishFidelityFixture.SharedSlotConfig);

    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
        => Task.FromResult<MealPlan?>(_plan);
    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default)
        => Task.FromResult(_plan);
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Recipe read model resolving the single fixture recipe ("Pasta Bake").</summary>
internal sealed class FixedRecipeReadModel : IRecipeReadModel
{
    private static readonly Guid RecipeId = TodayDishFidelityFixture.RecipeId;
    private static readonly RecipeReadModel FixtureModel =
        new(RecipeId, "Pasta Bake", [], DefaultServings: 2, HasPhoto: false);

    public Task<RecipeReadModel?> GetByIdAsync(Guid recipeId, CancellationToken ct = default)
        => Task.FromResult<RecipeReadModel?>(recipeId == RecipeId ? FixtureModel : null);
    public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string nameQuery, int maxResults = 20, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeReadModel>>([FixtureModel]);
    public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid recipeId, int servings, DateOnly today, CancellationToken ct = default)
        => Task.FromResult<RecipeDishEnrichment?>(
            recipeId == RecipeId
                ? new RecipeDishEnrichment(FulfillmentPercent: 100, TotalCost: null, CostIsPartial: false, HasExpiringIngredients: false)
                : null);
    public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid recipeId, int servings, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeMissingIngredient>>([]);
    public Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default)
        => Task.FromResult(false);
}

/// <summary>
/// Catalog-product reader for the fidelity fixture (plantry-nlg4). "Chicken thighs" resolves to
/// unit "lb"; "Rice" resolves a NAME but deliberately has no entry in the unit-code dictionary,
/// so the "?" fallback (AC4) is exercised on a resolvable-name-but-unresolvable-unit product.
/// </summary>
internal sealed class FixedCatalogProductReader : IMealPlanCatalogProductReader
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
            [TodayDishFidelityFixture.ChickenThighsProductId] = "Chicken thighs",
            [TodayDishFidelityFixture.RiceProductId] = "Rice",
        };
        return Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            productIds.Where(names.ContainsKey).ToDictionary(id => id, id => names[id]));
    }

    public Task<IReadOnlyDictionary<Guid, string>> ResolveDefaultUnitCodesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        // Rice is intentionally absent — its unit cannot be resolved (AC4: falls back to "?").
        var units = new Dictionary<Guid, string>
        {
            [TodayDishFidelityFixture.ChickenThighsProductId] = "lb",
        };
        return Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            productIds.Where(units.ContainsKey).ToDictionary(id => id, id => units[id]));
    }

    public Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
        IReadOnlyCollection<Guid> unitIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            unitIds.Where(id => id == TodayDishFidelityFixture.ChickenUnitId)
                .ToDictionary(id => id, _ => "lb"));
}

// ── AC5 fixture: note-based meal renders unaffected ──────────────────────────────

/// <summary>
/// L4 WebApplicationFactory for the AC5 regression check (plantry-nlg4): a note-based meal in the
/// Breakfast slot, Lunch/Dinner empty. Proves the per-dish refactor left note-meal rendering
/// untouched — Dishes stays empty for a note meal (nothing in the render path reads it: the note
/// text renders directly from <c>slot.Note</c>, and every Dishes-driven element is guarded by
/// <c>Note is null</c> or a dish count check that a zero-length list already satisfies as "hidden").
/// </summary>
public sealed class TodayNoteMealDishFidelityFactory : WebApplicationFactory<Program>
{
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
            services.AddSingleton<IClock>(TodayNoteMealDishFidelityFixture.Clock);

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
            services.AddSingleton<IMealSlotConfigRepository>(new NoteFixtureSlotConfigRepo());

            services.RemoveAll<IMealPlanRepository>();
            services.AddSingleton<IMealPlanRepository>(new NoteFixtureMealPlanRepo());

            services.RemoveAll<IRecipeReadModel>();
            services.AddSingleton<IRecipeReadModel>(new FixedRecipeReadModel());

            services.RemoveAll<IMealPlanStockReader>();
            services.AddSingleton<IMealPlanStockReader>(new FakeTodayNullStockReader());

            services.RemoveAll<IMealPlanCatalogProductReader>();
            services.AddSingleton<IMealPlanCatalogProductReader>(new FixedCatalogProductReader());

            // Cook/eaten status port (plantry-ohmb) — a note meal never has PlannedDishes, so the
            // batched pre-pass is empty and this reader is never called; IndexModel still needs an instance.
            services.RemoveAll<IMealPlanCookStatusReader>();
            services.AddSingleton<IMealPlanCookStatusReader>(new NullCookStatusReader());

            services.RemoveAll<Plantry.Planning.Application.IHouseholdMemberReader>();
            services.AddSingleton<Plantry.Planning.Application.IHouseholdMemberReader>(new FakeTodayPlannedBandMemberReader());

            TodayDealsStubs.RegisterEmpty(services);
            TodayWasteStatsStubs.RegisterEmpty(services);
        });
    }
}

public static class TodayNoteMealDishFidelityFixture
{
    public static readonly Guid HouseholdId = Guid.Parse("aa000003-0000-0000-0000-000000000003");
    private static readonly HouseholdId HhId = Plantry.SharedKernel.HouseholdId.From(HouseholdId);

    public static readonly IClock Clock = new SnapshotFixedClock(new DateOnly(2026, 6, 15));

    public static readonly MealSlotConfig SharedSlotConfig =
        MealSlotConfig.CreateWithDefaults(HhId, Clock);

    public static MealPlan BuildPlan(MealSlotConfig slotConfig)
    {
        var today = Clock.ToLocalDate(Clock.UtcNow);
        var plan = MealPlan.Start(HhId, today, Clock);

        var breakfast = slotConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();

        plan.AssignNote(
            today, breakfast.Id, "Leftover soup",
            attendeesOverride: null, source: "test", createdBy: Guid.Empty, Clock);

        return plan;
    }
}

internal sealed class NoteFixtureSlotConfigRepo : IMealSlotConfigRepository
{
    private readonly MealSlotConfig _config = TodayNoteMealDishFidelityFixture.SharedSlotConfig;

    public Task<MealSlotConfig?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        => Task.FromResult<MealSlotConfig?>(_config);
    public Task AddAsync(MealSlotConfig config, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class NoteFixtureMealPlanRepo : IMealPlanRepository
{
    private readonly MealPlan _plan =
        TodayNoteMealDishFidelityFixture.BuildPlan(TodayNoteMealDishFidelityFixture.SharedSlotConfig);

    public Task<IReadOnlyDictionary<Guid, PlannedMealSlotInfo>> FindSlotLabelsAsync(
        IReadOnlyList<Guid> plannedMealIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, PlannedMealSlotInfo>>(new Dictionary<Guid, PlannedMealSlotInfo>());

    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
        => Task.FromResult<MealPlan?>(_plan);
    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default)
        => Task.FromResult(_plan);
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}
