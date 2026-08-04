using System.Net;
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
using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Today;

/// <summary>
/// L4 regression coverage for plantry-nlg4's AC6: the Today band's product-dish name/unit
/// resolution must be one batched pair of calls for the whole page load — never a per-slot round
/// trip — mirroring the week-wide pre-pass plantry-vj6z established for MealPlan's LoadWeekAsync
/// (<see cref="Plantry.Web.Pages.MealPlan.IndexModel"/> Index.cshtml.cs:1076-1082) and the
/// equivalent regression suite for it, <c>MealPlanProductResolutionBatchingTests</c>.
///
/// Fixture: three distinct product dishes, one in each of today's three default slots
/// (Breakfast/Lunch/Dinner) — the minimum slot count that distinguishes a batched pre-pass
/// (1 call total) from a per-slot one (3 calls).
/// </summary>
public sealed class TodayProductResolutionBatchingTests
{
    [Fact(DisplayName = "GET /Today: product dishes across 3 slots resolve via exactly one batch call each")]
    public async Task ThreeSlotsWithProductDishes_ResolvesInOneBatchEach()
    {
        await using var factory = new TodayProductBatchingFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, TodayProductBatchingFixture.HouseholdId.ToString());

        var response = await client.GetAsync("/Today");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // AC6: one call each for the whole page load, regardless of how many of the three slots
        // (Breakfast, Lunch, Dinner) contain a product dish.
        Assert.Equal(1, factory.CatalogReader.ResolveNamesCallCount);
        Assert.Equal(1, factory.CatalogReader.ResolveUnitCodesCallCount);

        // Every product dish still renders its real name/unit in every slot — the "?" fallback or
        // "Unknown product" would appear if the batched dictionaries were empty or mis-keyed.
        Assert.Contains("Flour", html);
        Assert.Contains("Sugar", html);
        Assert.Contains("Butter", html);
        Assert.Contains("2 g", html);
        Assert.Contains("1 kg", html);
        Assert.Contains("3 ea", html);
    }

    [Fact(DisplayName = "GET /Today: a day with zero product dishes performs zero product-resolution calls")]
    public async Task ZeroProductDishes_PerformsZeroCalls()
    {
        await using var factory = new TodayRecipeOnlyBatchingFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, TodayProductBatchingFixture.HouseholdId.ToString());

        var response = await client.GetAsync("/Today");
        response.EnsureSuccessStatusCode();

        Assert.Equal(0, factory.CatalogReader.ResolveNamesCallCount);
        Assert.Equal(0, factory.CatalogReader.ResolveUnitCodesCallCount);
    }
}

// ── Fixture ───────────────────────────────────────────────────────────────────

internal static class TodayProductBatchingFixture
{
    public static readonly Guid HouseholdId = Guid.Parse("aa000004-0000-0000-0000-000000000004");
    private static readonly HouseholdId HhId = Plantry.SharedKernel.HouseholdId.From(HouseholdId);

    public static readonly IClock Clock = new SnapshotFixedClock(new DateOnly(2026, 6, 15));

    public static readonly MealSlotConfig SlotConfig = MealSlotConfig.CreateWithDefaults(HhId, Clock);

    public static readonly Guid FlourProductId = Guid.CreateVersion7();
    public static readonly Guid SugarProductId = Guid.CreateVersion7();
    public static readonly Guid ButterProductId = Guid.CreateVersion7();
    public static readonly Guid FlourUnitId = Guid.CreateVersion7();
    public static readonly Guid SugarUnitId = Guid.CreateVersion7();
    public static readonly Guid ButterUnitId = Guid.CreateVersion7();
    public static readonly Guid PancakesRecipeId = Guid.CreateVersion7();

    /// <summary>Breakfast=Flour, Lunch=Sugar, Dinner=Butter — one product dish per slot.</summary>
    public static MealPlan BuildProductPlan()
    {
        var today = Clock.ToLocalDate(Clock.UtcNow);
        var plan = MealPlan.Start(HhId, today, Clock);
        var ordered = SlotConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).ToList();

        plan.AssignMeal(today, ordered[0].Id, [DishSpec.ForProduct(FlourProductId, 2m, FlourUnitId)],
            null, "test", Guid.Empty, Clock);
        plan.AssignMeal(today, ordered[1].Id, [DishSpec.ForProduct(SugarProductId, 1m, SugarUnitId)],
            null, "test", Guid.Empty, Clock);
        plan.AssignMeal(today, ordered[2].Id, [DishSpec.ForProduct(ButterProductId, 3m, ButterUnitId)],
            null, "test", Guid.Empty, Clock);

        return plan;
    }

    /// <summary>Single recipe dish, zero product dishes — AC's "zero calls" counterpart.</summary>
    public static MealPlan BuildRecipeOnlyPlan()
    {
        var today = Clock.ToLocalDate(Clock.UtcNow);
        var plan = MealPlan.Start(HhId, today, Clock);
        var breakfast = SlotConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();

        plan.AssignMeal(today, breakfast.Id, [new DishSpec(DishKind.Recipe, PancakesRecipeId, 2)],
            null, "test", Guid.Empty, Clock);

        return plan;
    }
}

// ── Factories ─────────────────────────────────────────────────────────────────

public sealed class TodayProductBatchingFactory : WebApplicationFactory<Program>
{
    public TodayCountingCatalogProductReader CatalogReader { get; } = new();
    private readonly MealPlan _plan = TodayProductBatchingFixture.BuildProductPlan();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            TodayProductBatchingCommon.ConfigureSeams(
                services, _plan, TodayProductBatchingFixture.SlotConfig, TodayProductBatchingFixture.Clock,
                CatalogReader, new FixedRecipeReadModel(), new FakeTodayPlannedBandRecipeRepository());
        });
    }
}

public sealed class TodayRecipeOnlyBatchingFactory : WebApplicationFactory<Program>
{
    public TodayCountingCatalogProductReader CatalogReader { get; } = new();
    private readonly MealPlan _plan = TodayProductBatchingFixture.BuildRecipeOnlyPlan();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            TodayProductBatchingCommon.ConfigureSeams(
                services, _plan, TodayProductBatchingFixture.SlotConfig, TodayProductBatchingFixture.Clock,
                CatalogReader, new FixedRecipeReadModel(), new FakeTodayPlannedBandRecipeRepository());
        });
    }
}

/// <summary>
/// Shared seam wiring for the plantry-nlg4/plantry-r2yf Today batching regression suites — reused
/// (not duplicated) by <see cref="TodayProductBatchingFactory"/>/<see cref="TodayRecipeOnlyBatchingFactory"/>
/// (product-resolution batching, plantry-nlg4) and <see cref="TodayRecipeBatchingFactory"/>
/// (recipe-resolution batching, plantry-r2yf) — every seam except the six that genuinely vary by
/// scenario (plan, slot config, clock, product-catalog reader, recipe read model, recipe repository)
/// is registered here exactly once.
/// </summary>
internal static class TodayProductBatchingCommon
{
    public static void ConfigureSeams(
        IServiceCollection services,
        MealPlan plan,
        MealSlotConfig slotConfig,
        IClock clock,
        IMealPlanCatalogProductReader catalogReader,
        IRecipeReadModel recipeReadModel,
        IRecipeRepository recipeRepo)
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
        services.AddSingleton<IClock>(clock);

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
        services.AddSingleton(recipeRepo);

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
        services.AddSingleton<IMealSlotConfigRepository>(new TodayFixedPlanSlotConfigRepo(slotConfig));

        services.RemoveAll<IMealPlanRepository>();
        services.AddSingleton<IMealPlanRepository>(new TodayFixedPlanRepo(plan));

        services.RemoveAll<IRecipeReadModel>();
        services.AddSingleton(recipeReadModel);

        services.RemoveAll<IMealPlanStockReader>();
        services.AddSingleton<IMealPlanStockReader>(new FakeTodayNullStockReader());

        services.RemoveAll<IMealPlanCatalogProductReader>();
        services.AddSingleton<IMealPlanCatalogProductReader>(catalogReader);

        services.RemoveAll<Plantry.Planning.Application.IHouseholdMemberReader>();
        services.AddSingleton<Plantry.Planning.Application.IHouseholdMemberReader>(new FakeTodayPlannedBandMemberReader());

        TodayDealsStubs.RegisterEmpty(services);
    }
}

/// <summary>Returns the given <see cref="MealSlotConfig"/> for any household — shared by every
/// Today batching-regression scenario (plantry-nlg4/plantry-r2yf), parameterized on the config so
/// each scenario's own fixture (its own household/slot ids) can be plugged in.</summary>
internal sealed class TodayFixedPlanSlotConfigRepo(MealSlotConfig config) : IMealSlotConfigRepository
{
    public Task<MealSlotConfig?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        => Task.FromResult<MealSlotConfig?>(config);
    public Task AddAsync(MealSlotConfig config, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class TodayFixedPlanRepo(MealPlan plan) : IMealPlanRepository
{
    public Task<IReadOnlyDictionary<Guid, PlannedMealSlotInfo>> FindSlotLabelsAsync(
        IReadOnlyList<Guid> plannedMealIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, PlannedMealSlotInfo>>(new Dictionary<Guid, PlannedMealSlotInfo>());

    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
        => Task.FromResult<MealPlan?>(plan);
    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default)
        => Task.FromResult(plan);
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Catalog reader for the plantry-nlg4 batching scenario. Counts how many times
/// <see cref="ResolveNamesAsync"/> and <see cref="ResolveDefaultUnitCodesAsync"/> are invoked, so
/// AC6 can be asserted directly rather than only inferred from rendered output. Resolves
/// Flour -> "g", Sugar -> "kg", Butter -> "ea"; any other id falls back to "Unknown".
/// </summary>
public sealed class TodayCountingCatalogProductReader : IMealPlanCatalogProductReader
{
    public int ResolveNamesCallCount { get; private set; }
    public int ResolveUnitCodesCallCount { get; private set; }

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
        => Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            unitIds.ToDictionary(
                id => id,
                id => id == TodayProductBatchingFixture.FlourUnitId ? "g"
                    : id == TodayProductBatchingFixture.SugarUnitId ? "kg"
                    : id == TodayProductBatchingFixture.ButterUnitId ? "ea" : "?"));

    private static string ResolveName(Guid id) =>
        id == TodayProductBatchingFixture.FlourProductId ? "Flour"
        : id == TodayProductBatchingFixture.SugarProductId ? "Sugar"
        : id == TodayProductBatchingFixture.ButterProductId ? "Butter"
        : "Unknown product";

    private static string ResolveUnitCode(Guid id) =>
        id == TodayProductBatchingFixture.FlourProductId ? "g"
        : id == TodayProductBatchingFixture.SugarProductId ? "kg"
        : id == TodayProductBatchingFixture.ButterProductId ? "ea"
        : "?";
}
