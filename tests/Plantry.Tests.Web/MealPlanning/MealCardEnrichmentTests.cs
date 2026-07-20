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
using Plantry.Tests.Web.MealPlanning;
using Plantry.Tests.Web.Preferences;
using Plantry.Web.MealPlanning;
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

    /// <summary>
    /// A EUR household's meal-plan costs (per-meal card + week-total budget chip) render through MoneyDisplay
    /// with the '€' symbol rather than a hardcoded '$' (plantry-2x6e.2).
    /// </summary>
    [Fact(DisplayName = "GET /MealPlan renders € meal + week costs for a EUR household (plantry-2x6e.2)")]
    public async Task Get_MealPlan_Grid_Uses_Household_Display_Currency()
    {
        await using var eurFactory = new MealCardEnrichmentFactory(
            useExpiring: false, fulfillmentPct: 80, totalCost: 12.50m, displayCurrency: "EUR");
        var client = eurFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, EnrichmentFixture.HouseholdId.ToString());

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Both the per-meal card cost and the week-total budget chip render the EUR symbol. Read the decoded
        // text (the '€' is emitted HTML-encoded as &#x20AC;); the old '$12.50' must not appear.
        var text = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html).Body!.TextContent;
        Assert.Contains("€12.50", text);
        Assert.DoesNotContain("$12.50", text);
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
    private readonly string _displayCurrency;

    /// <summary>
    /// Parameterless constructor required by xUnit collection fixtures (only a single
    /// public constructor allowed, and it must be resolvable by xUnit's fixture runner).
    /// Configures the standard enrichment test case: 80%, $12.50 cost, hasExpiring=true, USD.
    /// </summary>
    public MealCardEnrichmentFactory()
        : this(useExpiring: true, fulfillmentPct: 80, totalCost: 12.50m) { }

    internal MealCardEnrichmentFactory(
        bool useExpiring, int fulfillmentPct, decimal totalCost, string displayCurrency = "USD")
    {
        _useExpiring = useExpiring;
        _fulfillmentPct = fulfillmentPct;
        _totalCost = totalCost;
        _displayCurrency = displayCurrency;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.AddFakeDisplayCurrency(_displayCurrency);
            services.AddFakeExpiringSoonHorizon();
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

            // ADR-021 week read model: provide a bag pre-populated with recipe/stock/price data
            // that makes the pure FulfillmentService.Compute / CostingService.Compute overloads
            // produce exactly (_fulfillmentPct, _totalCost, _useExpiring) without touching the DB.
            services.RemoveAll<IMealPlanWeekReadModel>();
            services.AddSingleton<IMealPlanWeekReadModel>(
                new FakeEnrichmentWeekReadModel(_useExpiring, _fulfillmentPct, _totalCost));

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

            // so5.5: stub ITagReader (needed by GeneratePlanService for unfulfillable tag name resolution)
            services.RemoveAll<ITagReader>();
            services.AddSingleton<ITagReader>(new NullTagReader());

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

    // so5.5: targeted full-corpus tag check — returns true for any tag (all cells are fulfillable in the enrichment test scenario).
    public Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default)
        => Task.FromResult(true);
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

/// <summary>
/// IMealPlanWeekReadModel fake that returns a pre-built WeekBag with deterministic recipe,
/// ingredient, product, stock, and price data so the pure FulfillmentService.Compute /
/// CostingService.Compute overloads produce exactly the enrichment values the test needs.
///
/// Layout for 80 % / $12.50 / hasExpiring=true (5 tracked ingredients, 4 in stock, 1 missing):
///   - 5 ingredients × $2.50 unit-price each → $12.50 total cost (all priced → CostIsPartial=false)
///   - product1 stock has SoonestExpiry = today+2 → HasExpiringIngredients=true
///
/// Layout for 100 % / $8.00 / hasExpiring=false (2 tracked ingredients, both in stock):
///   - 2 ingredients × $4.00 unit-price each → $8.00 total cost
/// </summary>
internal sealed class FakeEnrichmentWeekReadModel(bool useExpiring, int fulfillmentPct, decimal totalCost)
    : IMealPlanWeekReadModel
{
    // Stable IDs shared by all builders.
    private static readonly Guid UnitId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    // product ids used in the 80% scenario
    private static readonly Guid Prod1 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid Prod2 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Prod3 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003");
    private static readonly Guid Prod4 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000004");
    private static readonly Guid Prod5 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000005");

    // ingredient ids
    private static readonly Guid Ing1 = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid Ing2 = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
    private static readonly Guid Ing3 = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid Ing4 = Guid.Parse("cccccccc-0000-0000-0000-000000000004");
    private static readonly Guid Ing5 = Guid.Parse("cccccccc-0000-0000-0000-000000000005");

    public Task<WeekBag> LoadAsync(
        IReadOnlyList<Guid> recipeIds,
        IReadOnlyList<Guid> productIds,
        CancellationToken ct = default)
        => Task.FromResult(BuildBag());

    private WeekBag BuildBag()
    {
        // The unit "ea" — a simple count unit, base unit (FactorToBase=null, IsBase=true).
        // Since ingredient unit = stock unit = price unit, the converter always returns identity.
        var unitFact = new UnitFact(UnitId, "ea", "Each", "count", null, IsBase: true);
        var units = new Dictionary<Guid, UnitFact> { [UnitId] = unitFact };

        // Recipe: defaultServings=2 matches the meal plan fixture (servings=2).
        // scale = desiredServings / defaultServings = 2/2 = 1.0 — all quantities are net.
        var recipeFact = new RecipeFact(EnrichmentFixture.RecipeId, "Test Recipe", 2);
        var recipes = new Dictionary<Guid, RecipeFact> { [EnrichmentFixture.RecipeId] = recipeFact };

        // Build ingredients, products, stock, prices based on the desired enrichment scenario.
        List<IngredientFact> ingredients;
        Dictionary<Guid, ProductFact> productsDict;
        Dictionary<Guid, StockFact> stockDict;
        Dictionary<Guid, PriceFact> priceDict;

        if (fulfillmentPct == 80)
        {
            // 5 tracked ingredients, 4 in stock, 1 missing → 4/5 = 80 %.
            // All 5 priced at $2.50 each (scale=1, qty=1) → $12.50 total, CostIsPartial=false.
            var unitPrice = totalCost / 5m; // $2.50 per ingredient
            ingredients =
            [
                new(Ing1, EnrichmentFixture.RecipeId, Prod1, 1m, UnitId, 0),
                new(Ing2, EnrichmentFixture.RecipeId, Prod2, 1m, UnitId, 1),
                new(Ing3, EnrichmentFixture.RecipeId, Prod3, 1m, UnitId, 2),
                new(Ing4, EnrichmentFixture.RecipeId, Prod4, 1m, UnitId, 3),
                new(Ing5, EnrichmentFixture.RecipeId, Prod5, 1m, UnitId, 4),
            ];
            productsDict = new()
            {
                [Prod1] = MakeProduct(Prod1, "Prod1"),
                [Prod2] = MakeProduct(Prod2, "Prod2"),
                [Prod3] = MakeProduct(Prod3, "Prod3"),
                [Prod4] = MakeProduct(Prod4, "Prod4"),
                [Prod5] = MakeProduct(Prod5, "Prod5"),
            };
            // prod5 has no stock → Missing.
            var expiry = useExpiring ? DateOnly.FromDateTime(DateTime.Today.AddDays(2)) : (DateOnly?)null;
            stockDict = new()
            {
                // prod1: in stock with an imminent expiry (within 4 days) when useExpiring=true.
                [Prod1] = new StockFact(Prod1, [new StockLotFact(Prod1, UnitId, 2m)], expiry),
                [Prod2] = new StockFact(Prod2, [new StockLotFact(Prod2, UnitId, 2m)], null),
                [Prod3] = new StockFact(Prod3, [new StockLotFact(Prod3, UnitId, 2m)], null),
                [Prod4] = new StockFact(Prod4, [new StockLotFact(Prod4, UnitId, 2m)], null),
                // Prod5 intentionally absent → Missing.
            };
            priceDict = new()
            {
                [Prod1] = MakePrice(Prod1, unitPrice),
                [Prod2] = MakePrice(Prod2, unitPrice),
                [Prod3] = MakePrice(Prod3, unitPrice),
                [Prod4] = MakePrice(Prod4, unitPrice),
                [Prod5] = MakePrice(Prod5, unitPrice),
            };
        }
        else
        {
            // fulfillmentPct == 100: 2 tracked ingredients, both in stock, no expiring.
            // Priced at totalCost/2 each → totalCost total, CostIsPartial=false.
            var unitPrice = totalCost / 2m;
            ingredients =
            [
                new(Ing1, EnrichmentFixture.RecipeId, Prod1, 1m, UnitId, 0),
                new(Ing2, EnrichmentFixture.RecipeId, Prod2, 1m, UnitId, 1),
            ];
            productsDict = new()
            {
                [Prod1] = MakeProduct(Prod1, "Prod1"),
                [Prod2] = MakeProduct(Prod2, "Prod2"),
            };
            stockDict = new()
            {
                [Prod1] = new StockFact(Prod1, [new StockLotFact(Prod1, UnitId, 2m)], null),
                [Prod2] = new StockFact(Prod2, [new StockLotFact(Prod2, UnitId, 2m)], null),
            };
            priceDict = new()
            {
                [Prod1] = MakePrice(Prod1, unitPrice),
                [Prod2] = MakePrice(Prod2, unitPrice),
            };
        }

        var ingredientsByRecipe = new Dictionary<Guid, IReadOnlyList<IngredientFact>>
        {
            [EnrichmentFixture.RecipeId] = ingredients,
        };

        return new WeekBag(
            recipes,
            ingredientsByRecipe,
            productsDict,
            new Dictionary<Guid, IReadOnlyList<ConversionFact>>(), // no cross-dim conversions needed
            units,
            stockDict,
            priceDict);
    }

    private static ProductFact MakeProduct(Guid id, string name) =>
        new(id, name, TrackStock: true, DefaultUnitId: UnitId, ParentProductId: null,
            HasVariants: false, Archived: false, VariantProductIds: []);

    /// <summary>
    /// Price observation: price=unitPrice, quantity=1, unitId=UnitId, UnitPrice=unitPrice.
    /// Since UnitPrice is set, CostingService uses it directly without dividing Price/Quantity.
    /// </summary>
    private static PriceFact MakePrice(Guid productId, decimal unitPrice) =>
        new(productId, unitPrice, 1m, UnitId, unitPrice, DateTime.UtcNow.AddDays(-1));
}

// NullTagReader is defined in ConflictCellFragmentTests.cs (shared across the MealPlanning test namespace).
