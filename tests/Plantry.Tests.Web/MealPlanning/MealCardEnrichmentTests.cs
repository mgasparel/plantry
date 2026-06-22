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
using Plantry.Tests.Web.Preferences;
using Xunit;
using SharedSystemClock = Plantry.SharedKernel.Domain.SystemClock;

namespace Plantry.Tests.Web.MealPlanning;

/// <summary>
/// L4 fragment tests for meal card enrichment rendering (P3-4).
/// Verifies that the _MealCard partial renders live fulfillment %, cost, and Use-soon badge
/// when PlanFulfillmentService / PlanCostingService return non-null data.
/// Uses a WAF factory that injects a recipe reader returning a known RecipeDishEnrichment.
/// </summary>
[Collection(nameof(MealCardEnrichmentCollection))]
public sealed class MealCardEnrichmentTests(MealCardEnrichmentFactory factory)
{
    /// <summary>
    /// When a meal plan has a recipe dish with known enrichment (80%, $12.50, hasExpiring=true),
    /// the rendered grid must show the fulfilment percent, hi-level colour class (80 >= 80 = "hi"),
    /// cost, and Use-soon badge.
    /// </summary>
    [Fact(DisplayName = "GET /MealPlan grid shows live enrichment: fulfillment %, cost, Use-soon badge")]
    public async Task Get_MealPlan_Grid_Shows_Live_Enrichment()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, EnrichmentFixture.HouseholdId.ToString());

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Fulfillment percent rendered in the mc-fulf span
        Assert.Contains("80%", html);

        // Hi-level colour class applied (80 >= 80 qualifies as "hi" per FulfLevel)
        Assert.Contains("lvl-hi", html);

        // Cost rendered in mc-cost span — $12.50 (no ~ prefix since CostIsPartial=false)
        Assert.Contains("$12.50", html);

        // Use-soon badge rendered (HasExpiringIngredients=true)
        Assert.Contains("mc-soon", html);
        Assert.Contains("Use soon", html);
    }

    /// <summary>
    /// When a meal plan has a recipe dish with 100% fulfillment (no expiring), the mc-soon badge
    /// must NOT be present and the hi-level colour class must be applied.
    /// </summary>
    [Fact(DisplayName = "GET /MealPlan grid shows hi-level colour and no Use-soon when fully stocked")]
    public async Task Get_MealPlan_Grid_NoUseSoon_WhenFullyStocked()
    {
        await using var noExpiryFactory = new MealCardEnrichmentFactory(
            useExpiring: false, fulfillmentPct: 100, totalCost: 8.00m);
        var client = noExpiryFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, EnrichmentFixture.HouseholdId.ToString());

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("100%", html);
        Assert.Contains("lvl-hi", html);
        Assert.Contains("$8.00", html);
        Assert.DoesNotContain("mc-soon", html);
    }

    /// <summary>
    /// When the week cost roll-up produces a known total, the budget chip must show it.
    /// </summary>
    [Fact(DisplayName = "GET /MealPlan shows week cost total in budget chip")]
    public async Task Get_MealPlan_Shows_WeekCostTotal_In_BudgetChip()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, EnrichmentFixture.HouseholdId.ToString());

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Budget chip should show the week cost total (one meal = $12.50)
        Assert.Contains("budget-chip", html);
        Assert.Contains("$12.50", html); // the total
        Assert.Contains("/ wk", html);
    }
}

[CollectionDefinition(nameof(MealCardEnrichmentCollection))]
public sealed class MealCardEnrichmentCollection : ICollectionFixture<MealCardEnrichmentFactory> { }

/// <summary>
/// WAF factory that wires a meal plan containing a recipe dish with a known enrichment.
/// Uses <see cref="EnrichmentRecipeReader"/> to return a fixed <see cref="RecipeDishEnrichment"/>.
/// </summary>
public sealed class MealCardEnrichmentFactory : WebApplicationFactory<Program>
{
    private readonly bool _useExpiring;
    private readonly int _fulfillmentPct;
    private readonly decimal _totalCost;

    /// <summary>
    /// Parameterless constructor required by xUnit collection fixtures (only a single
    /// public constructor allowed, and it must be resolvable by xUnit's fixture runner).
    /// Configures the standard enrichment test case: 80%, $12.50 cost, hasExpiring=true.
    /// </summary>
    public MealCardEnrichmentFactory()
        : this(useExpiring: true, fulfillmentPct: 80, totalCost: 12.50m) { }

    internal MealCardEnrichmentFactory(bool useExpiring, int fulfillmentPct, decimal totalCost)
    {
        _useExpiring = useExpiring;
        _fulfillmentPct = fulfillmentPct;
        _totalCost = totalCost;
    }

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

            services.RemoveAll<UserManager<AppUser>>();
            services.AddSingleton<UserManager<AppUser>>(
                new FakeUserManager(new AppUser { Id = "00000000-0000-0000-0000-0000000000aa" }));

            // Meal plan repo: returns a plan with one recipe dish
            services.RemoveAll<IMealPlanRepository>();
            services.AddScoped<IMealPlanRepository>(_ =>
                new EnrichmentMealPlanRepo(EnrichmentFixture.RecipeId));

            services.RemoveAll<IMealSlotConfigRepository>();
            services.AddScoped<IMealSlotConfigRepository>(_ =>
                new FakeSlotRepo(EnrichmentFixture.SlotConfig));

            services.RemoveAll<IHouseholdMemberReader>();
            services.AddSingleton<IHouseholdMemberReader>(new FakeMemberReader([]));

            // Recipe reader: returns enrichment data for the recipe, plus the recipe display info
            services.RemoveAll<IRecipeReadModel>();
            services.AddSingleton<IRecipeReadModel>(new EnrichmentRecipeReader(
                EnrichmentFixture.RecipeId,
                new RecipeDishEnrichment(_fulfillmentPct, _totalCost, false, _useExpiring)));

            services.RemoveAll<IMealPlanCatalogProductReader>();
            services.AddSingleton<IMealPlanCatalogProductReader>(new FakeCatalogProductReaderW(existsResult: true));

            // P3-4 port interfaces — stock/price readers return null (no product dishes in this test)
            services.RemoveAll<IMealPlanStockReader>();
            services.AddSingleton<IMealPlanStockReader>(new NullStockReader());
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

            // P3-6a: stub AI planner + proposal store
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

            // P3-5: stub expiring-stock reader; re-register insights service
            services.RemoveAll<IMealPlanExpiringStockReader>();
            services.AddSingleton<IMealPlanExpiringStockReader>(new NullExpiringStockReader());
            services.RemoveAll<PlanInsightsService>();
            services.AddScoped<PlanInsightsService>();

            // plantry-so5.3: stub planning settings repos
            services.RemoveAll<IHouseholdPlanningSettingsRepository>();
            services.AddSingleton<IHouseholdPlanningSettingsRepository>(new NullPlanningSettingsRepo());
            services.RemoveAll<IWeekPlanningOverrideRepository>();
            services.AddSingleton<IWeekPlanningOverrideRepository>(new NullWeekOverrideRepo());
            services.RemoveAll<SetPlanningSettingsService>();
            services.AddScoped<SetPlanningSettingsService>();
        });
    }
}

// ── Enrichment test doubles ───────────────────────────────────────────────────

/// <summary>
/// Meal plan repo that returns a plan with one recipe dish.
/// The plan is seeded once per instance so the recipe dish is always present.
/// </summary>
internal sealed class EnrichmentMealPlanRepo(Guid recipeId) : IMealPlanRepository
{
    private readonly MealPlan _plan = BuildPlan(recipeId);

    private static MealPlan BuildPlan(Guid recipeId)
    {
        var hhId = SharedKernel.HouseholdId.From(EnrichmentFixture.HouseholdId);
        // Use a Monday in the past so it falls in "this week" window during tests
        var today = DateOnly.FromDateTime(DateTime.Today);
        var monday = MealPlan.NormalizeToMonday(today);
        var plan = MealPlan.Start(hhId, monday, SharedSystemClock.Instance);
        plan.AssignMeal(monday, EnrichmentFixture.SlotId, [new DishSpec(DishKind.Recipe, recipeId, 2)],
            null, "manual", Guid.Empty, SharedSystemClock.Instance);
        return plan;
    }

    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
        => Task.FromResult<MealPlan?>(_plan);

    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default)
        => Task.FromResult(_plan);

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Recipe reader that returns a specific <see cref="RecipeDishEnrichment"/> for a given recipe,
/// enabling the fulfillment/costing domain services to produce known rendered output.
/// </summary>
internal sealed class EnrichmentRecipeReader(Guid recipeId, RecipeDishEnrichment enrichment) : IRecipeReadModel
{
    public Task<RecipeReadModel?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<RecipeReadModel?>(id == recipeId
            ? new RecipeReadModel(id, "Test Recipe", [], 2)
            : null);

    public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string q, int max, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeReadModel>>(
            [new RecipeReadModel(recipeId, "Test Recipe", [], 2)]);

    public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid id, int servings, DateOnly today, CancellationToken ct = default)
        => Task.FromResult<RecipeDishEnrichment?>(id == recipeId ? enrichment : null);

    public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid id, int servings, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeMissingIngredient>>([]);
}

/// <summary>Shared stable identifiers for the enrichment test scenario.</summary>
internal static class EnrichmentFixture
{
    public static readonly Guid HouseholdId = Guid.Parse("22222222-0000-0000-0000-000000000002");
    public static readonly Guid RecipeId = Guid.Parse("33333333-0000-0000-0000-000000000003");

    private static readonly HouseholdId HhId = SharedKernel.HouseholdId.From(HouseholdId);
    public static readonly MealSlotConfig SlotConfig = MealSlotConfig.CreateWithDefaults(HhId, SharedSystemClock.Instance);
    public static readonly MealSlotId SlotId = SlotConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First().Id;
}
