using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Identity.Infrastructure;
using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.MealPlanning.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;
using Plantry.Tests.Web.Preferences;
using Plantry.Web.MealPlanning;
using Xunit;

namespace Plantry.Tests.Web.MealPlanning;

/// <summary>
/// Regression tests for the session-keyed pending proposal store (plantry-so5.2).
///
/// These tests use the REAL <see cref="DistributedCachePendingProposalStore"/> (not the singleton
/// fakes <see cref="PrimedPendingProposalStore"/>/<see cref="TwoProposalPendingStore"/> that ignore
/// the store key). They prove that proposals written under request A's store key are readable and
/// acceptable in subsequent requests (B, C) when the .AspNetCore.Session cookie flows on the client.
///
/// WHY THIS WAS MISSING: <see cref="PrimedPendingProposalStore"/> ignores <c>storeKey</c>
/// entirely — so the original tests passed even though the real store key rotated each request.
/// </summary>
[Collection(nameof(SessionKeyedStoreCollection))]
public sealed class SessionKeyedProposalStoreTests(SessionKeyedStoreFactory factory)
{
    private HttpClient CreateClient()
    {
        // AllowAutoRedirect=false so 302s are visible as assertions, not silently followed.
        // The WAF's default handler already shares a CookieContainer across requests within
        // the same HttpClient instance, so the .AspNetCore.Session cookie flows automatically.
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, SessionKeyedStoreFixture.HouseholdId.ToString());
        return client;
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the page.");
        return match.Groups[1].Value;
    }

    // ── Core session-key stability proof ──────────────────────────────────────

    /// <summary>
    /// Proves that a proposal generated in POST Generate (request A) is visible in a subsequent
    /// GET /MealPlan?handler=Grid (request B) when the session cookie flows on the client.
    /// This is the exact bug described in plantry-so5.2: without the sentinel write the
    /// Session.Id rotated each request, so request B always loaded an empty store.
    /// </summary>
    [Fact(DisplayName = "SO5.2 regression: proposal generated in request A is visible in request B (session key is stable)")]
    public async Task ProposalGeneratedInRequestA_IsVisibleInRequestB()
    {
        var client = CreateClient();

        // Request A: GET the page to obtain the antiforgery token.
        // This also starts the session (EnsureSessionStartedAsync writes the sentinel).
        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var week = SessionKeyedStoreFixture.WeekStart.ToString("yyyy-MM-dd");

        // Request A (continued): POST Generate — the planner returns one proposal; the store key
        // is stable because EnsureSessionStartedAsync wrote the sentinel and issued the cookie.
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });
        var generateResponse = await client.PostAsync($"/MealPlan?handler=Generate&week={week}", form);
        generateResponse.EnsureSuccessStatusCode();
        var generateHtml = await generateResponse.Content.ReadAsStringAsync();

        // The generate response itself must show the ghost cell.
        Assert.Contains("gh-tag", generateHtml);
        Assert.Contains(SessionKeyedStoreFixture.RecipeName, generateHtml);
        Assert.Contains("pending-bar", generateHtml);

        // Request B: GET the grid fragment — different request, same session cookie.
        // Without the bug fix, Session.Id would regenerate here, yielding a different store key
        // and an empty pending list → no ghost cells.
        var gridResponse = await client.GetAsync($"/MealPlan?handler=Grid&week={week}");
        gridResponse.EnsureSuccessStatusCode();
        var gridHtml = await gridResponse.Content.ReadAsStringAsync();

        // Ghost cell MUST be visible — proves the session key is stable across requests.
        Assert.Contains("gh-tag", gridHtml);
        Assert.Contains(SessionKeyedStoreFixture.RecipeName, gridHtml);
        Assert.Contains("pending-bar", gridHtml);
    }

    /// <summary>
    /// Proves the full generate→navigate→accept→verify loop:
    ///   1. POST Generate (request A) — proposal is stored under the session key.
    ///   2. GET /MealPlan (request B) — proposal is visible (ghost cell rendered).
    ///   3. POST AcceptCell (request C) — proposal is accepted; cell becomes a real planned meal.
    ///   4. GET /MealPlan (request D) — no more ghost cells; the accepted meal renders normally.
    ///
    /// This is the headline acceptance criterion from plantry-so5.2.
    /// </summary>
    [Fact(DisplayName = "SO5.2 regression: generate→navigate→accept loop — accepted cell becomes real meal, ghosts clear")]
    public async Task GenerateNavigateAccept_Loop_Works_EndToEnd()
    {
        var client = CreateClient();
        var week = SessionKeyedStoreFixture.WeekStart.ToString("yyyy-MM-dd");
        var slot = SessionKeyedStoreFixture.GhostSlot;

        // Step 1: GET page for antiforgery token.
        var pageHtml = await (await client.GetAsync($"/MealPlan?week={week}")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        // Step 2: POST Generate — stages one proposal.
        var generateForm = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });
        var generateResponse = await client.PostAsync($"/MealPlan?handler=Generate&week={week}", generateForm);
        generateResponse.EnsureSuccessStatusCode();

        // Step 3: GET /MealPlan — proposal must be visible (cross-request stability proof).
        var afterGenHtml = await (await client.GetAsync($"/MealPlan?week={week}")).Content.ReadAsStringAsync();
        Assert.Contains("gh-tag", afterGenHtml);
        Assert.Contains(SessionKeyedStoreFixture.RecipeName, afterGenHtml);

        // Grab a fresh antiforgery token for the POST AcceptCell.
        var acceptToken = ExtractAntiforgeryToken(afterGenHtml);

        var date = SessionKeyedStoreFixture.WeekStart.ToString("yyyy-MM-dd");

        // Step 4: POST AcceptCell — accept the one staged proposal.
        var acceptForm = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", acceptToken),
        });
        var acceptResponse = await client.PostAsync(
            $"/MealPlan?handler=AcceptCell&date={date}&slotId={slot.Id.Value:D}&week={week}", acceptForm);
        acceptResponse.EnsureSuccessStatusCode();

        // Step 5: GET /MealPlan — the accepted cell should no longer render as a ghost.
        // The pending-bar should have 0 proposals; the meal-card should appear.
        var afterAcceptHtml = await (await client.GetAsync($"/MealPlan?week={week}")).Content.ReadAsStringAsync();

        // No more ghost cells — the proposal was accepted.
        Assert.DoesNotContain("gh-tag", afterAcceptHtml);

        // Accepted meal renders as a real meal-card.
        Assert.Contains("meal-card", afterAcceptHtml);
    }

    /// <summary>
    /// Proves that when two proposals are staged, accepting one cell leaves the OTHER proposal
    /// visible in the next request. This is the "other pending suggestions survive" acceptance
    /// criterion from plantry-so5.2.
    /// </summary>
    [Fact(DisplayName = "SO5.2 regression: accept one cell — other pending suggestions survive across requests")]
    public async Task AcceptOneCell_OtherPendingSuggestionsRemain()
    {
        // This variant factory seeds TWO proposals on different days via a planner that proposes
        // on both Monday and Tuesday.
        await using var twoProposalFactory = new SessionKeyedTwoProposalFactory();
        var client = twoProposalFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, SessionKeyedStoreFixture.HouseholdId.ToString());

        var week = SessionKeyedStoreFixture.WeekStart.ToString("yyyy-MM-dd");

        // GET page for antiforgery token.
        var pageHtml = await (await client.GetAsync($"/MealPlan?week={week}")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        // POST Generate — both proposals are staged (planner returns two).
        var generateForm = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });
        var generateResponse = await client.PostAsync($"/MealPlan?handler=Generate&week={week}", generateForm);
        generateResponse.EnsureSuccessStatusCode();

        // Verify both proposals visible before accepting.
        var beforeHtml = await (await client.GetAsync($"/MealPlan?week={week}")).Content.ReadAsStringAsync();
        Assert.Contains(SessionKeyedTwoProposalFixture.RecipeDay0Name, beforeHtml);
        Assert.Contains(SessionKeyedTwoProposalFixture.RecipeDay1Name, beforeHtml);

        // Get fresh token.
        var acceptToken = ExtractAntiforgeryToken(beforeHtml);

        // Accept ONLY the Day0 (Monday) cell.
        var slot = SessionKeyedStoreFixture.GhostSlot;
        var day0 = SessionKeyedTwoProposalFixture.Day0.ToString("yyyy-MM-dd");
        var acceptForm = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", acceptToken),
        });
        var acceptResponse = await client.PostAsync(
            $"/MealPlan?handler=AcceptCell&date={day0}&slotId={slot.Id.Value:D}&week={week}", acceptForm);
        acceptResponse.EnsureSuccessStatusCode();

        // After accept: Day0 is gone as a ghost, Day1 proposal MUST survive.
        var afterHtml = await (await client.GetAsync($"/MealPlan?week={week}")).Content.ReadAsStringAsync();

        // Day1's ghost recipe must still appear.
        Assert.Contains(SessionKeyedTwoProposalFixture.RecipeDay1Name, afterHtml);

        // Day0 is now a real meal-card (no longer a ghost cell with that recipe as a ghost).
        // We verify the pending-bar still shows exactly 1 suggestion.
        Assert.Contains("1 suggestion", afterHtml);
    }
}

[CollectionDefinition(nameof(SessionKeyedStoreCollection))]
public sealed class SessionKeyedStoreCollection : ICollectionFixture<SessionKeyedStoreFactory> { }

// ── Fixture ───────────────────────────────────────────────────────────────────

internal static class SessionKeyedStoreFixture
{
    public static readonly Guid HouseholdId = Guid.Parse("22222222-0000-0000-0000-000000000002");
    public static readonly DateOnly WeekStart = new DateOnly(2026, 6, 16); // Monday

    public static readonly Guid RecipeId = Guid.Parse("ff000000-0000-0000-0000-000000000001");
    public const string RecipeName = "Session Test Recipe";

    public static MealSlot GhostSlot =>
        WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();
}

// ── Two-proposal fixture for the "other proposal survives" test ───────────────

internal static class SessionKeyedTwoProposalFixture
{
    public static readonly DateOnly Day0 = SessionKeyedStoreFixture.WeekStart;              // Monday — accepted
    public static readonly DateOnly Day1 = SessionKeyedStoreFixture.WeekStart.AddDays(1);   // Tuesday — must survive

    public static readonly Guid RecipeDay0Id = Guid.Parse("ff000000-0000-0000-0000-000000000001");
    public static readonly Guid RecipeDay1Id = Guid.Parse("ff000000-0000-0000-0000-000000000002");

    public const string RecipeDay0Name = "Session Day0 Recipe";
    public const string RecipeDay1Name = "Session Day1 Survivor Recipe";
}

// ── Planners for the tests ────────────────────────────────────────────────────

/// <summary>
/// Planner that returns one proposal on the first active slot of <see cref="SessionKeyedStoreFixture.WeekStart"/>.
/// Used by <see cref="SessionKeyedStoreFactory"/> to stage a single proposal via POST Generate.
/// </summary>
internal sealed class SingleProposalPlanner : IMealPlanner
{
    public Task<IReadOnlyList<ProposedMeal>> ProposeWeekAsync(
        IReadOnlyList<PlannerMealSlotContext> slots,
        PlanningWeights weights,
        CancellationToken ct = default)
    {
        var slot = WeekGridFixture.SharedConfig.Slots
            .Where(s => s.IsActive)
            .OrderBy(s => s.Ordinal)
            .First();

        // Propose only for the first slot on WeekStart — a single ghost cell.
        var ctx = slots.FirstOrDefault(s => s.Date == SessionKeyedStoreFixture.WeekStart && s.MealSlotId == slot.Id);
        if (ctx is null)
            return Task.FromResult<IReadOnlyList<ProposedMeal>>([]);

        var proposals = new List<ProposedMeal>
        {
            new(
                Date: ctx.Date,
                MealSlotId: ctx.MealSlotId,
                EffectiveAttendees: ctx.EffectiveAttendees,
                Dishes: [new ProposedDish(SessionKeyedStoreFixture.RecipeId, 4, 1)],
                Reasoning: "session-key-test")
        };
        return Task.FromResult<IReadOnlyList<ProposedMeal>>(proposals);
    }
}

/// <summary>
/// Planner that returns two proposals — one on Day0 (Monday) and one on Day1 (Tuesday) —
/// both on the first active slot. Used by <see cref="SessionKeyedTwoProposalFactory"/>
/// to stage two proposals via POST Generate.
/// </summary>
internal sealed class TwoProposalPlanner : IMealPlanner
{
    public Task<IReadOnlyList<ProposedMeal>> ProposeWeekAsync(
        IReadOnlyList<PlannerMealSlotContext> slots,
        PlanningWeights weights,
        CancellationToken ct = default)
    {
        var slot = WeekGridFixture.SharedConfig.Slots
            .Where(s => s.IsActive)
            .OrderBy(s => s.Ordinal)
            .First();

        var proposals = new List<ProposedMeal>();
        foreach (var ctx in slots)
        {
            if (ctx.MealSlotId != slot.Id) continue;
            if (ctx.Date == SessionKeyedTwoProposalFixture.Day0)
            {
                proposals.Add(new ProposedMeal(
                    ctx.Date, ctx.MealSlotId, ctx.EffectiveAttendees,
                    [new ProposedDish(SessionKeyedTwoProposalFixture.RecipeDay0Id, 4, 1)],
                    "day0"));
            }
            else if (ctx.Date == SessionKeyedTwoProposalFixture.Day1)
            {
                proposals.Add(new ProposedMeal(
                    ctx.Date, ctx.MealSlotId, ctx.EffectiveAttendees,
                    [new ProposedDish(SessionKeyedTwoProposalFixture.RecipeDay1Id, 4, 1)],
                    "day1"));
            }
        }
        return Task.FromResult<IReadOnlyList<ProposedMeal>>(proposals);
    }
}

// ── Recipe readers ────────────────────────────────────────────────────────────

/// <summary>Recipe reader that resolves the single session-key-test recipe.</summary>
internal sealed class SessionKeyedRecipeReader : IRecipeReadModel
{
    private static readonly RecipeReadModel _recipe = new(
        SessionKeyedStoreFixture.RecipeId,
        SessionKeyedStoreFixture.RecipeName,
        TagIds: [],
        DefaultServings: 4);

    public Task<RecipeReadModel?> GetByIdAsync(Guid recipeId, CancellationToken ct = default)
        => Task.FromResult<RecipeReadModel?>(recipeId == SessionKeyedStoreFixture.RecipeId ? _recipe : null);

    public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string q, int maxResults, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeReadModel>>([_recipe]);

    public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid id, int servings, DateOnly today, CancellationToken ct = default)
        => Task.FromResult<RecipeDishEnrichment?>(null);

    public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid id, int servings, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeMissingIngredient>>([]);

    // so5.5: targeted full-corpus tag check — returns true for any tag (all cells are fulfillable in this scenario).
    public Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default)
        => Task.FromResult(true);
}

/// <summary>Recipe reader that resolves both two-proposal recipes.</summary>
internal sealed class SessionKeyedTwoProposalRecipeReader : IRecipeReadModel
{
    private static readonly RecipeReadModel _day0 = new(
        SessionKeyedTwoProposalFixture.RecipeDay0Id,
        SessionKeyedTwoProposalFixture.RecipeDay0Name, [], 4);
    private static readonly RecipeReadModel _day1 = new(
        SessionKeyedTwoProposalFixture.RecipeDay1Id,
        SessionKeyedTwoProposalFixture.RecipeDay1Name, [], 4);

    public Task<RecipeReadModel?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        if (id == SessionKeyedTwoProposalFixture.RecipeDay0Id) return Task.FromResult<RecipeReadModel?>(_day0);
        if (id == SessionKeyedTwoProposalFixture.RecipeDay1Id) return Task.FromResult<RecipeReadModel?>(_day1);
        return Task.FromResult<RecipeReadModel?>(null);
    }

    public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string q, int maxResults, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeReadModel>>([_day0, _day1]);

    public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid id, int servings, DateOnly today, CancellationToken ct = default)
        => Task.FromResult<RecipeDishEnrichment?>(null);

    public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid id, int servings, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeMissingIngredient>>([]);

    // so5.5: targeted full-corpus tag check — returns true for any tag (all cells are fulfillable in this scenario).
    public Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default)
        => Task.FromResult(true);
}

// ── WAF factories ─────────────────────────────────────────────────────────────

/// <summary>
/// WAF factory for session-key stability tests.
/// Critical difference from all other planner WAF factories: registers the REAL
/// <see cref="DistributedCachePendingProposalStore"/> backed by an in-memory distributed cache,
/// so the actual session-keyed read/write path is exercised. The planner is a controllable fake
/// that returns a single known proposal.
/// </summary>
public sealed class SessionKeyedStoreFactory : WebApplicationFactory<Program>
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

            services.RemoveAll<UserManager<AppUser>>();
            services.AddSingleton<UserManager<AppUser>>(
                new FakeUserManager(new AppUser { Id = "00000000-0000-0000-0000-0000000000aa" }));

            // Singleton so the plan persists across requests (scoped would create a new
            // instance per request, losing the accepted meal between POST AcceptCell and the
            // subsequent GET /MealPlan).
            services.RemoveAll<IMealPlanRepository>();
            services.AddSingleton<IMealPlanRepository>(new SessionKeyedMealPlanRepo());

            services.RemoveAll<IMealSlotConfigRepository>();
            services.AddScoped<IMealSlotConfigRepository>(_ => new FakeSlotRepo(WeekGridFixture.SharedConfig));

            services.RemoveAll<IHouseholdMemberReader>();
            services.AddSingleton<IHouseholdMemberReader>(new FakeMemberReader(WeekGridFixture.Members));

            services.RemoveAll<IRecipeReadModel>();
            services.AddSingleton<IRecipeReadModel>(new SessionKeyedRecipeReader());

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

            // CRITICAL: Register the REAL DistributedCachePendingProposalStore with an in-memory
            // distributed cache. This is what makes this factory different from all the others —
            // the session-keyed store path is actually exercised, not bypassed by a fake that
            // ignores the storeKey. The in-memory cache is shared across requests within the WAF
            // because it is registered as a singleton.
            services.RemoveAll<IDistributedCache>();
            services.AddDistributedMemoryCache();
            services.RemoveAll<IPendingProposalStore>();
            services.AddSingleton<IPendingProposalStore, DistributedCachePendingProposalStore>();

            // Use the single-proposal planner so we have a controlled, deterministic result.
            services.RemoveAll<IMealPlanner>();
            services.AddSingleton<IMealPlanner>(new SingleProposalPlanner());

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
/// Variant factory for the two-proposal "other proposal survives" test.
/// Same as <see cref="SessionKeyedStoreFactory"/> but wires <see cref="TwoProposalPlanner"/>
/// and <see cref="SessionKeyedTwoProposalRecipeReader"/>.
/// </summary>
public sealed class SessionKeyedTwoProposalFactory : WebApplicationFactory<Program>
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

            services.RemoveAll<UserManager<AppUser>>();
            services.AddSingleton<UserManager<AppUser>>(
                new FakeUserManager(new AppUser { Id = "00000000-0000-0000-0000-0000000000aa" }));

            // Singleton so the plan persists across requests (accepted meal visible on subsequent GETs).
            services.RemoveAll<IMealPlanRepository>();
            services.AddSingleton<IMealPlanRepository>(new SessionKeyedMealPlanRepo());

            services.RemoveAll<IMealSlotConfigRepository>();
            services.AddScoped<IMealSlotConfigRepository>(_ => new FakeSlotRepo(WeekGridFixture.SharedConfig));

            services.RemoveAll<IHouseholdMemberReader>();
            services.AddSingleton<IHouseholdMemberReader>(new FakeMemberReader(WeekGridFixture.Members));

            services.RemoveAll<IRecipeReadModel>();
            services.AddSingleton<IRecipeReadModel>(new SessionKeyedTwoProposalRecipeReader());

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

            // REAL store with in-memory cache — same as SessionKeyedStoreFactory.
            services.RemoveAll<IDistributedCache>();
            services.AddDistributedMemoryCache();
            services.RemoveAll<IPendingProposalStore>();
            services.AddSingleton<IPendingProposalStore, DistributedCachePendingProposalStore>();

            services.RemoveAll<IMealPlanner>();
            services.AddSingleton<IMealPlanner>(new TwoProposalPlanner());

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
/// Mutable in-memory meal plan repo used by the session-key tests.
/// Holds the committed plan so AcceptCell can persist the accepted meal across the
/// AcceptProposalService → SaveChangesAsync → FindByWeekAsync read-back cycle.
/// </summary>
internal sealed class SessionKeyedMealPlanRepo : IMealPlanRepository
{
    private readonly Dictionary<string, MealPlan> _plans = [];
    private static readonly IClock _clock = Plantry.SharedKernel.Domain.SystemClock.Instance;

    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
    {
        _plans.TryGetValue(PlanKey(householdId, weekStart), out var plan);
        return Task.FromResult(plan);
    }

    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default)
    {
        var key = PlanKey(householdId, weekStart);
        if (!_plans.TryGetValue(key, out var plan))
        {
            plan = MealPlan.Start(householdId, weekStart, clock);
            _plans[key] = plan;
        }
        return Task.FromResult(plan);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

    private static string PlanKey(HouseholdId h, DateOnly w) => $"{h.Value:N}_{w:yyyyMMdd}";
}

// NullTagReader is defined in ConflictCellFragmentTests.cs (shared across the MealPlanning test namespace).
