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
using Plantry.Web.MealPlanning;
using Xunit;

namespace Plantry.Tests.Web.MealPlanning;

/// <summary>
/// L4 fragment tests for P3-6a ghost cells and pending bar.
/// Validates that when pending proposals are in the store the grid renders:
///   - ghost-class cells with ".gh-tag" "Suggested" badge, dish names, Accept + Reject buttons
///   - a ".pending-bar" with the correct suggestion count and Accept-all / Discard buttons
/// Also validates that POST /MealPlan?handler=Generate returns a 200 with the week grid.
/// </summary>
[Collection(nameof(GhostCellCollection))]
public sealed class GhostCellFragmentTests(GhostCellFactory factory)
{
    private HttpClient CreateClient()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, WeekGridFixture.HouseholdId.ToString());
        return client;
    }

    // ── Ghost cells render when proposals are pending ─────────────────────────

    [Fact(DisplayName = "GET /MealPlan grid fragment shows gh-tag Suggested badge when proposals pending")]
    public async Task Grid_WithPendingProposals_RendersSuggestedBadge()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/MealPlan?handler=Grid");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("gh-tag", html);
        Assert.Contains("Suggested", html);
    }

    [Fact(DisplayName = "GET /MealPlan grid shows dish name in ghost cell")]
    public async Task Grid_WithPendingProposals_ShowsDishName()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/MealPlan?handler=Grid");
        var html = await response.Content.ReadAsStringAsync();

        // The factory seeds GhostCellFixture.RecipeName
        Assert.Contains(GhostCellFixture.RecipeName, html);
    }

    [Fact(DisplayName = "GET /MealPlan grid ghost cell has Accept and Reject htmx buttons")]
    public async Task Grid_GhostCell_HasAcceptAndRejectButtons()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/MealPlan?handler=Grid");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("handler=AcceptCell", html);
        Assert.Contains("handler=RejectCell", html);
        Assert.Contains("Accept", html);
        // Reject button class (was gh-reject; renamed to "gh-btn icon reject" in plantry-v0r)
        Assert.Contains("class=\"gh-btn icon reject\"", html);
    }

    // ── Pending bar renders with correct count ────────────────────────────────

    [Fact(DisplayName = "GET /MealPlan grid shows pending-bar when proposals exist")]
    public async Task Grid_WithPendingProposals_ShowsPendingBar()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/MealPlan?handler=Grid");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("pending-bar", html);
    }

    [Fact(DisplayName = "GET /MealPlan grid pending-bar shows '1 suggestion' text")]
    public async Task Grid_PendingBar_ShowsOneSuggestion()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/MealPlan?handler=Grid");
        var html = await response.Content.ReadAsStringAsync();

        // 1 proposal seeded → "1 suggestion" (not "suggestions")
        Assert.Contains("1 suggestion", html);
    }

    [Fact(DisplayName = "GET /MealPlan grid pending-bar has Accept-all and Discard buttons")]
    public async Task Grid_PendingBar_HasAcceptAllAndDiscardButtons()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/MealPlan?handler=Grid");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("handler=AcceptAll", html);
        Assert.Contains("handler=Discard", html);
        Assert.Contains("Accept all", html);
        Assert.Contains("Discard", html);
    }

    // ── Ghost cell has .mcell.ghost class ─────────────────────────────────────

    [Fact(DisplayName = "GET /MealPlan grid renders mcell with ghost class for pending cell")]
    public async Task Grid_PendingCell_HasGhostClass()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/MealPlan?handler=Grid");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("mcell ghost", html);
    }

    // ── POST Generate returns 200 with the grid ───────────────────────────────

    [Fact(DisplayName = "POST /MealPlan?handler=Generate returns 200 with week grid")]
    public async Task PostGenerate_Returns200WithGrid()
    {
        var client = CreateClient();

        // GET page first to obtain antiforgery token + paired cookie
        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var match = System.Text.RegularExpressions.Regex.Match(
            pageHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the page.");
        var token = match.Groups[1].Value;

        var week = GhostCellFixture.WeekStart.ToString("yyyy-MM-dd");
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });

        var response = await client.PostAsync($"/MealPlan?handler=Generate&week={week}", form);

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Response is the _WeekGrid partial
        Assert.Contains("wkgrid", html);
    }
}

[CollectionDefinition(nameof(GhostCellCollection))]
public sealed class GhostCellCollection : ICollectionFixture<GhostCellFactory> { }

/// <summary>
/// WAF factory that pre-seeds one pending proposal so the grid renders ghost cells.
/// Uses an in-memory <see cref="IPendingProposalStore"/> stub that returns a fixed proposal.
/// </summary>
public sealed class GhostCellFactory : WebApplicationFactory<Program>
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

            // Stub UserManager
            services.RemoveAll<UserManager<AppUser>>();
            services.AddSingleton<UserManager<AppUser>>(
                new FakeUserManager(new AppUser { Id = "00000000-0000-0000-0000-0000000000aa" }));

            services.RemoveAll<IMealPlanRepository>();
            services.AddScoped<IMealPlanRepository>(_ => new FakeMealPlanRepo());

            services.RemoveAll<IMealSlotConfigRepository>();
            services.AddScoped<IMealSlotConfigRepository>(_ => new FakeSlotRepo(WeekGridFixture.SharedConfig));

            services.RemoveAll<IHouseholdMemberReader>();
            services.AddSingleton<IHouseholdMemberReader>(new FakeMemberReader(WeekGridFixture.Members));

            services.RemoveAll<IRecipeReadModel>();
            services.AddSingleton<IRecipeReadModel>(new GhostCellRecipeReader());

            services.RemoveAll<IMealPlanCatalogProductReader>();
            services.AddSingleton<IMealPlanCatalogProductReader>(new FakeProductReader([]));

            services.RemoveAll<AssignMealService>();
            services.AddScoped<AssignMealService>();
            services.RemoveAll<MoveMealService>();
            services.AddScoped<MoveMealService>();

            services.RemoveAll<IMealPlanStockReader>();
            services.AddSingleton<IMealPlanStockReader>(new NullStockReader());
            services.RemoveAll<IMealPlanPriceReader>();
            services.AddSingleton<IMealPlanPriceReader>(new NullPriceReader());
            services.RemoveAll<IMealPlanShoppingWriter>();
            services.AddSingleton<IMealPlanShoppingWriter>(new NullShoppingWriter());

            // ADR-021 week read model: return empty bag — no DB connection in WAF tests.
            services.RemoveAll<IMealPlanWeekReadModel>();
            services.AddSingleton<IMealPlanWeekReadModel>(new NullWeekReadModel());

            services.RemoveAll<PlanFulfillmentService>();
            services.AddScoped<PlanFulfillmentService>();
            services.RemoveAll<PlanCostingService>();
            services.AddScoped<PlanCostingService>();
            services.RemoveAll<ShopForWeekService>();
            services.AddScoped<ShopForWeekService>();

            // P3-6a: seed one pending proposal via a primed proposal store
            services.RemoveAll<IMealPlanner>();
            services.AddSingleton<IMealPlanner>(new NullMealPlanner());
            services.RemoveAll<IPendingProposalStore>();
            services.AddSingleton<IPendingProposalStore>(new PrimedPendingProposalStore());
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

// ── GhostCellFixture ──────────────────────────────────────────────────────────

internal static class GhostCellFixture
{
    /// <summary>Monday of the current ISO week — kept dynamic so the proposal date always
    /// falls within the week the server renders on today's GET /MealPlan.</summary>
    public static DateOnly WeekStart
    {
        get
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var offset = ((int)today.DayOfWeek + 6) % 7; // days since Monday
            return today.AddDays(-offset);
        }
    }

    public static readonly Guid RecipeId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    public const string RecipeName = "Test Ghost Recipe";

    /// <summary>The slot used for the seeded pending proposal.</summary>
    public static MealSlot GhostSlot =>
        WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();

    public static ProposedMeal SeedProposal => new(
        Date: WeekStart,
        MealSlotId: GhostSlot.Id,
        EffectiveAttendees: [],
        Dishes: [new ProposedDish(RecipeId, 4, 1)],
        Reasoning: "AI test reasoning");
}

/// <summary>
/// A pending proposal store that returns <see cref="GhostCellFixture.SeedProposal"/>
/// for any key — simulates the state after AI generation populated proposals.
/// </summary>
internal sealed class PrimedPendingProposalStore : IPendingProposalStore
{
    public Task<IReadOnlyList<ProposedMeal>> GetAsync(string storeKey, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ProposedMeal>>([GhostCellFixture.SeedProposal]);

    public Task SetAsync(string storeKey, IReadOnlyList<ProposedMeal> proposals, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task RemoveAsync(string storeKey, DateOnly date, MealSlotId slotId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task ClearAsync(string storeKey, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>Recipe reader that resolves GhostCellFixture.RecipeId to the fixture recipe.</summary>
internal sealed class GhostCellRecipeReader : IRecipeReadModel
{
    private static readonly RecipeReadModel Recipe = new(
        GhostCellFixture.RecipeId, GhostCellFixture.RecipeName, [], DefaultServings: 4);

    public Task<RecipeReadModel?> GetByIdAsync(Guid recipeId, CancellationToken ct = default)
        => Task.FromResult<RecipeReadModel?>(recipeId == GhostCellFixture.RecipeId ? Recipe : null);

    public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string nameQuery, int maxResults, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeReadModel>>([Recipe]);

    public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid recipeId, int servings, DateOnly today, CancellationToken ct = default)
        => Task.FromResult<RecipeDishEnrichment?>(null);

    public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid recipeId, int servings, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeMissingIngredient>>([]);

    // so5.5: targeted full-corpus tag check — the ghost cell recipe has no tags, so returns false.
    public Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default)
        => Task.FromResult(false);
}

// NullTagReader is defined in ConflictCellFragmentTests.cs (shared across the MealPlanning test namespace).

// ── Mixed-cost ghost-cell partial flag tests ──────────────────────────────────

/// <summary>
/// Regression guard for BuildGhostEnrichmentFromBag's "mixed priced/unpriced → CostIsPartial"
/// rule. A ghost proposal with one priced recipe and one unpriced recipe must render "~$"
/// (the partial-cost prefix) rather than "$" (the complete-cost prefix).
/// </summary>
[Collection(nameof(MixedCostGhostCollection))]
public sealed class MixedCostGhostCellTests(MixedCostGhostFactory factory)
{
    [Fact(DisplayName = "Ghost cell with one priced + one unpriced recipe renders ~$ partial-cost prefix")]
    public async Task GhostCell_WithMixedPricedUnpricedRecipes_ShowsPartialCostPrefix()
    {
        var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, WeekGridFixture.HouseholdId.ToString());

        var response = await client.GetAsync("/MealPlan?handler=Grid");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        // The ghost cell must display the partial-cost prefix — proving CostIsPartial == true.
        Assert.Contains("~$", html);
    }
}

[CollectionDefinition(nameof(MixedCostGhostCollection))]
public sealed class MixedCostGhostCollection : ICollectionFixture<MixedCostGhostFactory> { }

/// <summary>
/// WAF factory that seeds a two-dish ghost proposal:
///   - Recipe A: has one ingredient that is stocked and priced → TotalCost non-null
///   - Recipe B: has one ingredient that is stocked but NOT priced → TotalCost null
/// With the fix, BuildGhostEnrichmentFromBag should set anyPriced=true, anyUnpriced=true
/// → costIsPartial = true → _GhostCell.cshtml renders "~$".
/// </summary>
public sealed class MixedCostGhostFactory : WebApplicationFactory<Program>
{
    // Stable IDs for the mixed-cost scenario.
    internal static readonly Guid RecipeAId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");
    internal static readonly Guid RecipeBId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000002");
    private static readonly Guid ProdAId    = Guid.Parse("ffffffff-0000-0000-0000-000000000001");
    private static readonly Guid ProdBId    = Guid.Parse("ffffffff-0000-0000-0000-000000000002");
    private static readonly Guid IngAId     = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid IngBId     = Guid.Parse("11111111-0000-0000-0000-000000000002");
    private static readonly Guid UnitId     = Guid.Parse("22222222-0000-0000-0000-000000000001");

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

            services.RemoveAll<IMealPlanRepository>();
            services.AddScoped<IMealPlanRepository>(_ => new FakeMealPlanRepo());

            services.RemoveAll<IMealSlotConfigRepository>();
            services.AddScoped<IMealSlotConfigRepository>(_ => new FakeSlotRepo(WeekGridFixture.SharedConfig));

            services.RemoveAll<IHouseholdMemberReader>();
            services.AddSingleton<IHouseholdMemberReader>(new FakeMemberReader(WeekGridFixture.Members));

            // Two-recipe reader: both resolve to a recipe name.
            services.RemoveAll<IRecipeReadModel>();
            services.AddSingleton<IRecipeReadModel>(new TwoRecipeReadModel());

            services.RemoveAll<IMealPlanCatalogProductReader>();
            services.AddSingleton<IMealPlanCatalogProductReader>(new FakeProductReader([]));

            services.RemoveAll<AssignMealService>();
            services.AddScoped<AssignMealService>();
            services.RemoveAll<MoveMealService>();
            services.AddScoped<MoveMealService>();

            services.RemoveAll<IMealPlanStockReader>();
            services.AddSingleton<IMealPlanStockReader>(new NullStockReader());
            services.RemoveAll<IMealPlanPriceReader>();
            services.AddSingleton<IMealPlanPriceReader>(new NullPriceReader());
            services.RemoveAll<IMealPlanShoppingWriter>();
            services.AddSingleton<IMealPlanShoppingWriter>(new NullShoppingWriter());

            // ADR-021: provide a bag where Recipe A is priced and Recipe B is not.
            services.RemoveAll<IMealPlanWeekReadModel>();
            services.AddSingleton<IMealPlanWeekReadModel>(BuildMixedCostReadModel());

            services.RemoveAll<PlanFulfillmentService>();
            services.AddScoped<PlanFulfillmentService>();
            services.RemoveAll<PlanCostingService>();
            services.AddScoped<PlanCostingService>();
            services.RemoveAll<ShopForWeekService>();
            services.AddScoped<ShopForWeekService>();

            // Seed a two-dish pending proposal: Recipe A + Recipe B.
            services.RemoveAll<IMealPlanner>();
            services.AddSingleton<IMealPlanner>(new NullMealPlanner());
            services.RemoveAll<IPendingProposalStore>();
            services.AddSingleton<IPendingProposalStore>(new TwoDishPendingProposalStore());
            services.RemoveAll<GeneratePlanService>();
            services.AddScoped<GeneratePlanService>();
            services.RemoveAll<AcceptProposalService>();
            services.AddScoped<AcceptProposalService>();

            services.RemoveAll<IUserPreferenceRepository>();
            services.AddSingleton<IUserPreferenceRepository>(new NullPrefsRepo());

            services.RemoveAll<ITagReader>();
            services.AddSingleton<ITagReader>(new NullTagReader());

            services.RemoveAll<IMealPlanExpiringStockReader>();
            services.AddSingleton<IMealPlanExpiringStockReader>(new NullExpiringStockReader());
            services.RemoveAll<PlanInsightsService>();
            services.AddScoped<PlanInsightsService>();

            services.RemoveAll<IHouseholdPlanningSettingsRepository>();
            services.AddSingleton<IHouseholdPlanningSettingsRepository>(new NullPlanningSettingsRepo());
            services.RemoveAll<IWeekPlanningOverrideRepository>();
            services.AddSingleton<IWeekPlanningOverrideRepository>(new NullWeekOverrideRepo());
            services.RemoveAll<SetPlanningSettingsService>();
            services.AddScoped<SetPlanningSettingsService>();
        });
    }

    /// <summary>
    /// Builds a WeekBag where:
    ///   - Recipe A: 1 ingredient (ProdA, qty=1), ProdA is stocked (qty=2) and priced ($5.00).
    ///   - Recipe B: 1 ingredient (ProdB, qty=1), ProdB is stocked (qty=2) but NOT priced.
    /// After enrichment: A.TotalCost = $5.00, B.TotalCost = null.
    /// BuildGhostEnrichmentFromBag → anyPriced=true, anyUnpriced=true → CostIsPartial=true → "~$".
    /// </summary>
    private IMealPlanWeekReadModel BuildMixedCostReadModel()
    {
        var unitFact = new UnitFact(UnitId, "ea", "Each", "count", null, IsBase: true);
        var units    = new Dictionary<Guid, UnitFact> { [UnitId] = unitFact };

        var recipes = new Dictionary<Guid, RecipeFact>
        {
            [RecipeAId] = new RecipeFact(RecipeAId, "Recipe A", DefaultServings: 2),
            [RecipeBId] = new RecipeFact(RecipeBId, "Recipe B", DefaultServings: 2),
        };

        var ingredientsByRecipe = new Dictionary<Guid, IReadOnlyList<IngredientFact>>
        {
            [RecipeAId] = [new IngredientFact(IngAId, RecipeAId, ProdAId, 1m, UnitId, 0)],
            [RecipeBId] = [new IngredientFact(IngBId, RecipeBId, ProdBId, 1m, UnitId, 0)],
        };

        ProductFact MakeProd(Guid id, string name) =>
            new(id, name, TrackStock: true, DefaultUnitId: UnitId, ParentProductId: null,
                HasVariants: false, Archived: false, VariantProductIds: []);

        var products = new Dictionary<Guid, ProductFact>
        {
            [ProdAId] = MakeProd(ProdAId, "Product A"),
            [ProdBId] = MakeProd(ProdBId, "Product B"),
        };

        var stock = new Dictionary<Guid, StockFact>
        {
            [ProdAId] = new StockFact(ProdAId, [new StockLotFact(ProdAId, UnitId, 2m)], null),
            [ProdBId] = new StockFact(ProdBId, [new StockLotFact(ProdBId, UnitId, 2m)], null),
        };

        // ProdA priced at $5.00; ProdB deliberately has no price entry.
        var prices = new Dictionary<Guid, PriceFact>
        {
            [ProdAId] = new PriceFact(ProdAId, 5m, 1m, UnitId, 5m, DateTime.UtcNow.AddDays(-1)),
        };

        var bag = new WeekBag(recipes, ingredientsByRecipe, products,
            new Dictionary<Guid, IReadOnlyList<ConversionFact>>(), units, stock, prices);

        return new FixedBagReadModel(bag);
    }
}

/// <summary>Returns the same <see cref="WeekBag"/> for every <c>LoadAsync</c> call.</summary>
internal sealed class FixedBagReadModel(WeekBag bag) : IMealPlanWeekReadModel
{
    public Task<WeekBag> LoadAsync(
        IReadOnlyList<Guid> recipeIds,
        IReadOnlyList<Guid> productIds,
        CancellationToken ct = default)
        => Task.FromResult(bag);
}

/// <summary>Resolves both RecipeA and RecipeB by ID; used by MixedCostGhostFactory.</summary>
internal sealed class TwoRecipeReadModel : IRecipeReadModel
{
    private static readonly RecipeReadModel RecipeA = new(
        MixedCostGhostFactory.RecipeAId, "Recipe A", [], DefaultServings: 2);
    private static readonly RecipeReadModel RecipeB = new(
        MixedCostGhostFactory.RecipeBId, "Recipe B", [], DefaultServings: 2);

    public Task<RecipeReadModel?> GetByIdAsync(Guid recipeId, CancellationToken ct = default)
    {
        if (recipeId == MixedCostGhostFactory.RecipeAId) return Task.FromResult<RecipeReadModel?>(RecipeA);
        if (recipeId == MixedCostGhostFactory.RecipeBId) return Task.FromResult<RecipeReadModel?>(RecipeB);
        return Task.FromResult<RecipeReadModel?>(null);
    }

    public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string nameQuery, int maxResults, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeReadModel>>([RecipeA, RecipeB]);

    public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid recipeId, int servings, DateOnly today, CancellationToken ct = default)
        => Task.FromResult<RecipeDishEnrichment?>(null);

    public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid recipeId, int servings, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeMissingIngredient>>([]);

    public Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default)
        => Task.FromResult(false);
}

/// <summary>
/// Pending proposal store that returns a two-dish proposal (Recipe A + Recipe B)
/// for the MixedCostGhostFactory scenario.
/// </summary>
internal sealed class TwoDishPendingProposalStore : IPendingProposalStore
{
    private static ProposedMeal BuildProposal()
    {
        var slot = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var monday = today.AddDays(-((int)today.DayOfWeek + 6) % 7);
        return new ProposedMeal(
            Date: monday,
            MealSlotId: slot.Id,
            EffectiveAttendees: [],
            Dishes: [
                new ProposedDish(MixedCostGhostFactory.RecipeAId, 2, 0),
                new ProposedDish(MixedCostGhostFactory.RecipeBId, 2, 1),
            ],
            Reasoning: "mixed-cost test");
    }

    public Task<IReadOnlyList<ProposedMeal>> GetAsync(string storeKey, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ProposedMeal>>([BuildProposal()]);

    public Task SetAsync(string storeKey, IReadOnlyList<ProposedMeal> proposals, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task RemoveAsync(string storeKey, DateOnly date, MealSlotId slotId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task ClearAsync(string storeKey, CancellationToken ct = default)
        => Task.CompletedTask;
}
