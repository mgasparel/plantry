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
    public static readonly DateOnly WeekStart = new DateOnly(2026, 6, 16); // A Monday
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
}
