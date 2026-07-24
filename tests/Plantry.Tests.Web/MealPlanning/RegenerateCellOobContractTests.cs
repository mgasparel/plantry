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
/// ADR-013 OOB-contract tests for POST RegenerateCell (P3-6b, J8).
///
/// Acceptance criteria:
///   1. POST RegenerateCell re-emits #plan-rail out-of-band (OobContract).
///   2. POST RegenerateCell touches ONLY the one pending cell — the response is a cell fragment,
///      not a full-region repaint of the week grid.
///   3. POST RegenerateCell returns 200 even when no pending proposal exists for the cell
///      (idempotent — the store remove is a no-op and a new proposal is staged).
/// </summary>
[Collection(nameof(RegenerateCellCollection))]
public sealed class RegenerateCellOobContractTests(RegenerateCellFactory factory)
{
    private HttpClient CreateClient()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, WeekGridFixture.HouseholdId.ToString());
        return client;
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the page.");
        return match.Groups[1].Value;
    }

    /// <summary>Monday of the current ISO week as ISO-8601 string, matching the server's default week.</summary>
    private static string CurrentMondayIso
    {
        get
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var offset = ((int)today.DayOfWeek + 6) % 7;
            return today.AddDays(-offset).ToString("yyyy-MM-dd");
        }
    }

    /// <summary>Wednesday of the current ISO week (Day0+2) — used for "empty cell" target in merge-safety tests.</summary>
    private static string CurrentWednesdayIso
    {
        get
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var offset = ((int)today.DayOfWeek + 6) % 7;
            return today.AddDays(-offset + 2).ToString("yyyy-MM-dd");
        }
    }

    // ── 1. OobContract: RegenerateCell carries plan-rail ──────────────────────

    [Fact(DisplayName = "POST RegenerateCell re-emits #plan-rail out-of-band (OobContract — ADR-013)")]
    public async Task PostRegenerateCell_CarriesPlanRailProjection()
    {
        var client = CreateClient();
        var slot = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });

        var response = await client.PostAsync(
            $"/MealPlan?handler=RegenerateCell&date={CurrentMondayIso}&slotId={slot.Id.Value:D}", form);

        response.EnsureSuccessStatusCode();
        var fragment = await response.Content.ReadAsStringAsync();

        // ADR-013 OOB-contract: mutation response must carry the plan-rail projection.
        // plantry-khw/plantry-pg6: also carries plan-bar-nav, plan-cost-chip, plan-bar-autofill projections.
        OobContract.AssertCarriesProjections(fragment, "plan-rail", "plan-bar-nav", "plan-cost-chip", "plan-bar-autofill");
    }

    // ── 1b. OobContract: GenerateCell (per-cell empty auto-fill) carries plan-bar projections ──

    [Fact(DisplayName = "POST GenerateCell re-emits #plan-rail and plan-bar projections out-of-band (OobContract — ADR-013 / plantry-khw/plantry-pg6)")]
    public async Task PostGenerateCell_CarriesPlanRailProjection()
    {
        var client = CreateClient();
        var slot = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });

        // Target an EMPTY cell (no seeded proposal) — per-cell "Auto-fill" on an empty slot.
        var response = await client.PostAsync(
            $"/MealPlan?handler=GenerateCell&date={CurrentWednesdayIso}&slotId={slot.Id.Value:D}", form);

        response.EnsureSuccessStatusCode();
        var fragment = await response.Content.ReadAsStringAsync();

        // ADR-013 OOB-contract: mutation response must carry the plan-rail projection.
        // plantry-khw/plantry-pg6: also carries plan-bar-nav, plan-cost-chip, plan-bar-autofill.
        OobContract.AssertCarriesProjections(fragment, "plan-rail", "plan-bar-nav", "plan-cost-chip", "plan-bar-autofill");
    }

    // ── 1c. Merge safety: GenerateCell on an empty cell preserves other proposals ──

    [Fact(DisplayName = "POST GenerateCell preserves other pending proposals (does not wipe them)")]
    public async Task PostGenerateCell_OtherProposalsSurvive()
    {
        // Two proposals are seeded (Day0 Monday, Day1 Tuesday). Auto-filling a DIFFERENT,
        // empty cell (Day2 Wednesday, same slot) must not wipe either existing proposal —
        // the same merge guard that RegenerateCell relies on (passes 1 & 2 fixed exactly
        // this "generate wipes other pending" failure mode for the sibling handlers).
        await using var twoProposalFactory = new TwoProposalRegenerateFactory();
        var client = twoProposalFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, WeekGridFixture.HouseholdId.ToString());

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        Assert.Contains(TwoProposalFixture.RecipeDay0Name, pageHtml);
        Assert.Contains(TwoProposalFixture.RecipeDay1Name, pageHtml);

        var slot = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });

        // Auto-fill the empty Wednesday cell — neither seeded proposal lives here.
        var response = await client.PostAsync(
            $"/MealPlan?handler=GenerateCell&date={CurrentWednesdayIso}&slotId={slot.Id.Value:D}", form);

        response.EnsureSuccessStatusCode();

        // Both pre-existing proposals must still render after generating an unrelated empty cell.
        var afterPageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        Assert.Contains(TwoProposalFixture.RecipeDay0Name, afterPageHtml);
        Assert.Contains(TwoProposalFixture.RecipeDay1Name, afterPageHtml);
    }

    // ── 2. Cell fragment, not full-region repaint ─────────────────────────────

    [Fact(DisplayName = "POST RegenerateCell returns a cell fragment, not a full wkgrid repaint")]
    public async Task PostRegenerateCell_ReturnsFragmentNotFullGrid()
    {
        var client = CreateClient();
        var slot = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });

        var response = await client.PostAsync(
            $"/MealPlan?handler=RegenerateCell&date={CurrentMondayIso}&slotId={slot.Id.Value:D}", form);

        response.EnsureSuccessStatusCode();
        var fragment = await response.Content.ReadAsStringAsync();

        // Must NOT be a full region repaint — only the cell + OOB rail, not the whole grid.
        OobContract.AssertNoFullRepaint(fragment, "wkgrid");
    }

    // ── 3. Returns 200 ────────────────────────────────────────────────────────

    [Fact(DisplayName = "POST RegenerateCell returns 200")]
    public async Task PostRegenerateCell_Returns200()
    {
        var client = CreateClient();
        var slot = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });

        var response = await client.PostAsync(
            $"/MealPlan?handler=RegenerateCell&date={CurrentMondayIso}&slotId={slot.Id.Value:D}", form);

        response.EnsureSuccessStatusCode();
    }

    // ── 4. Merge safety: other proposals survive regenerate ───────────────────

    [Fact(DisplayName = "POST RegenerateCell preserves other pending proposals (does not wipe them)")]
    public async Task PostRegenerateCell_OtherProposalsSurvive()
    {
        // Use a factory that seeds TWO proposals — one on day 0 and one on day 1.
        // Regenerating day 0 must leave day 1's ghost dish name visible in the page.
        await using var twoProposalFactory = new TwoProposalRegenerateFactory();
        var client = twoProposalFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, WeekGridFixture.HouseholdId.ToString());

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        // Both proposals should appear in the initial grid
        Assert.Contains(TwoProposalFixture.RecipeDay0Name, pageHtml);
        Assert.Contains(TwoProposalFixture.RecipeDay1Name, pageHtml);

        var slots = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).ToList();
        var slot = slots.First();

        // Regenerate the Day0 cell — Day1 proposal must NOT disappear
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });

        // RegenerateCell on the Monday cell (day 0 of week)
        var response = await client.PostAsync(
            $"/MealPlan?handler=RegenerateCell&date={TwoProposalFixture.Day0:yyyy-MM-dd}&slotId={slot.Id.Value:D}", form);

        response.EnsureSuccessStatusCode();

        // The cell fragment + OOB rail won't necessarily contain the day1 name, but the
        // surviving proposal must appear when we reload the full grid. Load the grid.
        var afterPageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();

        // The Day1 ghost must still render — its dish name must appear in the grid.
        Assert.Contains(TwoProposalFixture.RecipeDay1Name, afterPageHtml);
    }
}

[CollectionDefinition(nameof(RegenerateCellCollection))]
public sealed class RegenerateCellCollection : ICollectionFixture<RegenerateCellFactory> { }

// ── TwoProposal merge-safety fixture ──────────────────────────────────────────

/// <summary>
/// Two pending proposals on different days of the same week.
/// Day0 = current-week Monday; Day1 = current-week Tuesday.
/// Dates are kept dynamic so proposals always fall within the week the server renders.
/// The merge-safety test regenerates Day0 and asserts Day1 recipe name survives.
/// </summary>
internal static class TwoProposalFixture
{
    private static DateOnly CurrentMonday
    {
        get
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var offset = ((int)today.DayOfWeek + 6) % 7;
            return today.AddDays(-offset);
        }
    }

    public static DateOnly WeekStart => CurrentMonday;
    public static DateOnly Day0 => CurrentMonday;           // regenerated
    public static DateOnly Day1 => CurrentMonday.AddDays(1); // must survive

    public const string RecipeDay0Name = "Regen Target Dish";
    public const string RecipeDay1Name = "Survivor Ghost Dish";

    public static readonly Guid RecipeDay0Id = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");
    public static readonly Guid RecipeDay1Id = Guid.Parse("eeeeeeee-0000-0000-0000-000000000002");

    public static MealSlot Slot =>
        WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();

    public static ProposedMeal ProposalDay0 => new(
        Date: Day0,
        MealSlotId: Slot.Id,
        EffectiveAttendees: [],
        Dishes: [new ProposedDish(RecipeDay0Id, 4, 1)],
        Reasoning: "day0 proposal");

    public static ProposedMeal ProposalDay1 => new(
        Date: Day1,
        MealSlotId: Slot.Id,
        EffectiveAttendees: [],
        Dishes: [new ProposedDish(RecipeDay1Id, 4, 1)],
        Reasoning: "day1 proposal");
}

/// <summary>
/// A mutable in-memory proposal store seeded with two proposals.
/// Supports full SetAsync/GetAsync/RemoveAsync so the merge-safety test round-trips through the
/// real OnPostRegenerateCellAsync merge logic.
/// </summary>
internal sealed class TwoProposalPendingStore : IPendingProposalStore
{
    private readonly List<ProposedMeal> _proposals = [TwoProposalFixture.ProposalDay0, TwoProposalFixture.ProposalDay1];

    public Task<IReadOnlyList<ProposedMeal>> GetAsync(string storeKey, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ProposedMeal>>(_proposals.ToList());

    public Task SetAsync(string storeKey, IReadOnlyList<ProposedMeal> proposals, CancellationToken ct = default)
    {
        _proposals.Clear();
        _proposals.AddRange(proposals);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string storeKey, DateOnly date, MealSlotId slotId, CancellationToken ct = default)
    {
        _proposals.RemoveAll(p => p.Date == date && p.MealSlotId == slotId);
        return Task.CompletedTask;
    }

    public Task ClearAsync(string storeKey, CancellationToken ct = default)
    {
        _proposals.Clear();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Recipe reader that resolves both TwoProposalFixture recipe IDs.
/// Used by the merge-safety test so ghost dish names render in the grid.
/// </summary>
internal sealed class TwoProposalRecipeReader : IRecipeReadModel
{
    private static readonly RecipeReadModel RecipeDay0 = new(
        TwoProposalFixture.RecipeDay0Id, TwoProposalFixture.RecipeDay0Name, [], DefaultServings: 4);
    private static readonly RecipeReadModel RecipeDay1 = new(
        TwoProposalFixture.RecipeDay1Id, TwoProposalFixture.RecipeDay1Name, [], DefaultServings: 4);

    public Task<RecipeReadModel?> GetByIdAsync(Guid recipeId, CancellationToken ct = default)
    {
        if (recipeId == TwoProposalFixture.RecipeDay0Id) return Task.FromResult<RecipeReadModel?>(RecipeDay0);
        if (recipeId == TwoProposalFixture.RecipeDay1Id) return Task.FromResult<RecipeReadModel?>(RecipeDay1);
        return Task.FromResult<RecipeReadModel?>(null);
    }

    public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string nameQuery, int maxResults, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeReadModel>>([RecipeDay0, RecipeDay1]);

    public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid recipeId, int servings, DateOnly today, CancellationToken ct = default)
        => Task.FromResult<RecipeDishEnrichment?>(null);

    public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid recipeId, int servings, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeMissingIngredient>>([]);

    // so5.5: targeted full-corpus tag check — returns true for any tag (all cells are fulfillable in the regenerate test scenario).
    public Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default)
        => Task.FromResult(true);
}

/// <summary>WAF for the merge-safety test. Uses TwoProposalPendingStore (mutable) + TwoProposalRecipeReader.</summary>
public sealed class TwoProposalRegenerateFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.AddFakeDisplayCurrency();
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

            services.RemoveAll<IMealPlanRepository>();
            services.AddScoped<IMealPlanRepository>(_ => new FakeMealPlanRepo());

            services.RemoveAll<IMealSlotConfigRepository>();
            services.AddScoped<IMealSlotConfigRepository>(_ => new FakeSlotRepo(WeekGridFixture.SharedConfig));

            services.RemoveAll<IHouseholdMemberReader>();
            services.AddSingleton<IHouseholdMemberReader>(new FakeMemberReader(WeekGridFixture.Members));

            services.RemoveAll<IRecipeReadModel>();
            services.AddSingleton<IRecipeReadModel>(new TwoProposalRecipeReader());

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
            services.RemoveAll<IMealPlanCookStatusReader>();
            services.AddSingleton<IMealPlanCookStatusReader>(new NullCookStatusReader());

            // ADR-021 week read model: return empty bag — no DB connection in WAF tests.
            services.RemoveAll<IMealPlanWeekReadModel>();
            services.AddSingleton<IMealPlanWeekReadModel>(new NullWeekReadModel());

            services.RemoveAll<PlanFulfillmentService>();
            services.AddScoped<PlanFulfillmentService>();
            services.RemoveAll<PlanCostingService>();
            services.AddScoped<PlanCostingService>();
            services.RemoveAll<ShopForWeekService>();
            services.AddScoped<ShopForWeekService>();

            // TwoProposalPendingStore is mutable and singleton so state persists across requests.
            services.RemoveAll<IMealPlanner>();
            services.AddSingleton<IMealPlanner>(new NullMealPlanner());
            services.RemoveAll<IPendingProposalStore>();
            services.AddSingleton<IPendingProposalStore>(new TwoProposalPendingStore());
            services.RemoveAll<GeneratePlanService>();
            services.AddScoped<GeneratePlanService>();
            services.RemoveAll<AcceptProposalService>();
            services.AddScoped<AcceptProposalService>();

            services.RemoveAll<IUserPreferenceRepository>();
            services.AddSingleton<IUserPreferenceRepository>(new NullPrefsRepo());

            // so5.5: stub ITagReader (needed by GeneratePlanService for unfulfillable tag name resolution)
            services.RemoveAll<ITagReader>();
            services.AddSingleton<ITagReader>(new NullTagReader());

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

/// <summary>
/// WAF factory for RegenerateCell OOB contract tests.
/// Uses a primed pending proposal store (same as GhostCellFactory) so there is an existing
/// proposal for the cell being regenerated, plus a UserManager stub for the POST handler.
/// </summary>
public sealed class RegenerateCellFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.AddFakeDisplayCurrency();
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
            services.RemoveAll<IMealPlanCookStatusReader>();
            services.AddSingleton<IMealPlanCookStatusReader>(new NullCookStatusReader());

            // ADR-021 week read model: return empty bag — no DB connection in WAF tests.
            services.RemoveAll<IMealPlanWeekReadModel>();
            services.AddSingleton<IMealPlanWeekReadModel>(new NullWeekReadModel());

            services.RemoveAll<PlanFulfillmentService>();
            services.AddScoped<PlanFulfillmentService>();
            services.RemoveAll<PlanCostingService>();
            services.AddScoped<PlanCostingService>();
            services.RemoveAll<ShopForWeekService>();
            services.AddScoped<ShopForWeekService>();

            // Pre-seeded proposal store; NullMealPlanner so re-generation produces no proposal
            // (empty result is valid — the cell simply becomes empty after regenerate)
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

// NullTagReader is defined in ConflictCellFragmentTests.cs (shared across the MealPlanning test namespace).
