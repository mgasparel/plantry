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
/// ADR-013 OOB-contract tests for the meal editor (plantry-cyj).
///
/// Acceptance criteria:
///   1. No client-side rollup formula — OnPostRollupAsync returns _EditorRollup for server-computed
///      fulfillment/cost; the response does NOT carry a full-region repaint.
///   2. Editor save (POST Assign) and clear (POST Clear) route through CellFragmentAsync and carry
///      the #plan-rail projection, asserted by the shared OobContract primitive.
///   3. The editor partial (GET Editor) renders the Alpine component scaffold (meal-editor-inner
///      root, ed-rollup container for the server-swap target, Save/Clear action wiring).
/// </summary>
public sealed class MealEditorOobContractTests : IClassFixture<MealEditorOobContractFactory>
{
    private readonly MealEditorOobContractFactory _factory;

    public MealEditorOobContractTests(MealEditorOobContractFactory factory) => _factory = factory;

    private HttpClient CreateClient()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, WeekGridFixture.HouseholdId.ToString());
        return client;
    }

    // ── 1. Rollup handler: server-computed, no client formula ────────────────

    [Fact(DisplayName = "POST Rollup returns _EditorRollup fragment for an empty dish list")]
    public async Task PostRollup_EmptyDishList_ReturnsRollupFragment()
    {
        var client = CreateClient();
        var slot = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            new KeyValuePair<string, string>("mode", "dishes"),
        });

        var response = await client.PostAsync(
            $"/MealPlan?handler=Rollup&date=2026-06-01&slotId={slot.Id.Value:D}", form);

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Empty dish list → "Add a dish to see fulfillment & cost" hint
        Assert.Contains("Add a dish to see fulfillment", html);

        // Must NOT be a full region repaint — the response is _EditorRollup only, not a cell swap.
        OobContract.AssertNoFullRepaint(html, "wkgrid");
        OobContract.AssertNoFullRepaint(html, "plan-rail");
    }

    [Fact(DisplayName = "POST Rollup in note mode returns note hint")]
    public async Task PostRollup_NoteMode_ReturnsNoteHint()
    {
        var client = CreateClient();
        var slot = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            new KeyValuePair<string, string>("mode", "note"),
        });

        var response = await client.PostAsync(
            $"/MealPlan?handler=Rollup&date=2026-06-01&slotId={slot.Id.Value:D}", form);

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("A note keeps the slot", html);
    }

    // ── 2. OobContract: Assign and Clear carry plan-rail ────────────────────

    [Fact(DisplayName = "POST Assign re-emits #plan-rail out-of-band (OobContract — editor path)")]
    public async Task PostAssign_CarriesPlanRailProjection()
    {
        var client = CreateClient();
        var slot = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            new KeyValuePair<string, string>("mode", "note"),
            new KeyValuePair<string, string>("note", "Editor OOB test"),
        });

        var response = await client.PostAsync(
            $"/MealPlan?handler=Assign&date=2026-06-01&slotId={slot.Id.Value:D}", form);

        response.EnsureSuccessStatusCode();
        var fragment = await response.Content.ReadAsStringAsync();

        // ADR-013 OOB-contract: mutation response must carry the plan-rail projection.
        // plantry-khw: also carries plan-bar-nav, plan-bar-cost, plan-bar-autofill projections.
        OobContract.AssertCarriesProjections(fragment, "plan-rail", "plan-bar-nav", "plan-bar-cost", "plan-bar-autofill");
    }

    [Fact(DisplayName = "POST Clear re-emits #plan-rail and plan-bar projections out-of-band (OobContract — editor path)")]
    public async Task PostClear_CarriesPlanRailProjection()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, MealEditorFixture.HouseholdId.ToString());

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var slot = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();

        var response = await client.PostAsync(
            $"/MealPlan?handler=Clear&date=2026-06-01&slotId={slot.Id.Value:D}&mealId={MealEditorFixture.SeedMealId:D}",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
            }));

        response.EnsureSuccessStatusCode();
        var fragment = await response.Content.ReadAsStringAsync();

        // ADR-013 OOB-contract: mutation response must carry rail and plan-bar projections.
        // plantry-khw: plan-bar-nav/cost/autofill are now re-emitted alongside every cell mutation.
        OobContract.AssertCarriesProjections(fragment, "plan-rail", "plan-bar-nav", "plan-bar-cost", "plan-bar-autofill");
    }

    // ── 3. Editor partial scaffold ───────────────────────────────────────────

    [Fact(DisplayName = "GET Editor returns editor partial with meal-editor-inner root and ed-rollup container")]
    public async Task GetEditor_ReturnsEditorScaffold()
    {
        var client = CreateClient();
        var slot = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();

        var response = await client.GetAsync(
            $"/MealPlan?handler=Editor&date=2026-06-01&slotId={slot.Id.Value:D}");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Editor root carries the Alpine.data component registration reference
        Assert.Contains("meal-editor-inner", html);
        Assert.Contains("mealEditor(", html);

        // Rollup container present for the server-swap target (ADR-013 §4/§5)
        Assert.Contains("ed-rollup-", html);

        // Save and cancel buttons
        Assert.Contains("Save meal", html);
        Assert.Contains("Cancel", html);

        // No inline client formula — the 'roll' getter must NOT be in the response
        Assert.DoesNotContain("get roll()", html);
        Assert.DoesNotContain("mirrors PL.rollup", html);
    }

    // ── 4. Initial rollup on GET Editor for existing meal (regression guard) ─

    [Fact(DisplayName = "GET Editor for existing dish meal renders initial rollup from server (no client formula)")]
    public async Task GetEditor_ExistingDishMeal_RendersInitialRollup()
    {
        // Uses a variant factory that seeds a recipe-dish meal and returns enrichment data,
        // so fulfillmentService.RollUpMealAsync returns a non-zero percent on open.
        await using var factory = new ExistingDishMealEditorFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, WeekGridFixture.HouseholdId.ToString());

        var slot = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();

        // GET Editor for the seeded meal — use the repo's exposed meal ID so it matches
        var mealId = factory.Repo.SeedMealId;
        var response = await client.GetAsync(
            $"/MealPlan?handler=Editor&date=2026-06-01&slotId={slot.Id.Value:D}&mealId={mealId:D}");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // The initial rollup section must render fulfillment info — not the empty-state hint.
        // If the regression recurs, this shows "Add a dish to see fulfillment & cost" instead.
        Assert.DoesNotContain("Add a dish to see fulfillment", html);
        Assert.Contains("in your pantry", html);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the page.");
        return match.Groups[1].Value;
    }
}

// ── Fixture / Factory ─────────────────────────────────────────────────────────

internal static class MealEditorFixture
{
    public static readonly Guid HouseholdId = WeekGridFixture.HouseholdId;
    public static readonly Guid SeedMealId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000099");
}

/// <summary>
/// Factory for meal editor OOB contract tests. Extends WeekGridFragmentFactory with:
///   - A real UserManager stub (POST handlers call GetCurrentUserIdAsync)
///   - A seeded plan repo that has one meal so POST Clear has something to remove
/// </summary>
public sealed class MealEditorOobContractFactory : WeekGridFragmentFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            // POST Assign and POST Clear resolve the current user — stub UserManager.
            services.RemoveAll<UserManager<AppUser>>();
            services.AddSingleton<UserManager<AppUser>>(
                new FakeUserManager(new AppUser { Id = "00000000-0000-0000-0000-0000000000aa" }));

            // Seeded repo: one note meal on 2026-06-01 in slot 0, used by POST Clear test.
            services.RemoveAll<IMealPlanRepository>();
            services.AddScoped<IMealPlanRepository>(_ => new SeededMealEditorRepo());
        });
    }
}

/// <summary>
/// Meal plan repo seeded with one note meal so POST Clear can find a meal to remove.
/// </summary>
internal sealed class SeededMealEditorRepo : IMealPlanRepository
{
    private static readonly IClock _clock = SystemClock.Instance;
    private MealPlan? _plan;

    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
    {
        _plan ??= BuildPlan(householdId, weekStart);
        return Task.FromResult<MealPlan?>(_plan);
    }

    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default)
    {
        _plan ??= BuildPlan(householdId, weekStart);
        return Task.FromResult(_plan);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

    private static MealPlan BuildPlan(HouseholdId householdId, DateOnly weekStart)
    {
        var monday = MealPlan.NormalizeToMonday(weekStart == default
            ? DateOnly.Parse("2026-06-01")
            : weekStart);
        var slot = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();
        var plan = MealPlan.Start(householdId, monday, _clock);
        plan.AssignNote(DateOnly.Parse("2026-06-01"), slot.Id, "ClearTestNote", null, "manual", Guid.Empty, _clock);
        return plan;
    }
}

/// <summary>
/// Factory for the "GET Editor for existing dish meal renders initial rollup" regression test.
/// Seeds a plan with a recipe dish meal and stubs the recipe reader to return enrichment data
/// (so fulfillmentService.RollUpMealAsync returns a non-zero percent on the GET Editor open).
/// </summary>
public sealed class ExistingDishMealEditorFactory : WeekGridFragmentFactory
{
    private static readonly Guid _recipeId = Guid.Parse("aaaaaaaa-bbbb-0000-0000-000000000001");

    // Expose the repo so tests can retrieve the dynamically-assigned meal ID.
    public ExistingDishMealRepo Repo { get; } = new(_recipeId);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            // Seed a plan with one recipe-dish meal
            services.RemoveAll<IMealPlanRepository>();
            services.AddScoped<IMealPlanRepository>(_ => Repo);

            // Recipe reader returns enrichment with 75% fulfillment so the rollup shows percent
            services.RemoveAll<IRecipeReadModel>();
            services.AddSingleton<IRecipeReadModel>(new EnrichedRecipeReader(_recipeId));
        });
    }
}

public sealed class ExistingDishMealRepo : IMealPlanRepository
{
    private static readonly IClock _clock = SystemClock.Instance;
    private readonly MealPlan _plan;

    public ExistingDishMealRepo(Guid recipeId)
    {
        // Eagerly build the plan so SeedMealId is available before any HTTP request.
        var hhId = SharedKernel.HouseholdId.From(WeekGridFixture.HouseholdId);
        var monday = MealPlan.NormalizeToMonday(DateOnly.Parse("2026-06-01"));
        var slot = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();
        _plan = MealPlan.Start(hhId, monday, _clock);
        _plan.AssignMeal(DateOnly.Parse("2026-06-01"), slot.Id,
            [new DishSpec(DishKind.Recipe, recipeId, 2)],
            null, "manual", Guid.Empty, _clock);
    }

    /// <summary>The ID of the seeded meal — available immediately (plan is built in constructor).</summary>
    public Guid SeedMealId => _plan.PlannedMeals.First().Id.Value;

    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
        => Task.FromResult<MealPlan?>(_plan);

    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default)
        => Task.FromResult(_plan);

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Recipe reader that returns a recipe with 75% fulfillment so the editor shows "75% in your pantry"
/// on open for an existing dish meal (regression guard for the initial rollup fix in plantry-cyj).
/// </summary>
internal sealed class EnrichedRecipeReader(Guid enrichedRecipeId) : IRecipeReadModel
{
    public Task<RecipeReadModel?> GetByIdAsync(Guid recipeId, CancellationToken ct = default)
        => Task.FromResult<RecipeReadModel?>(new RecipeReadModel(enrichedRecipeId, "Test Recipe", [], 2));

    public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string nameQuery, int maxResults, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeReadModel>>([new RecipeReadModel(enrichedRecipeId, "Test Recipe", [], 2)]);

    public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid recipeId, int servings, DateOnly today, CancellationToken ct = default)
    {
        if (recipeId != enrichedRecipeId) return Task.FromResult<RecipeDishEnrichment?>(null);
        return Task.FromResult<RecipeDishEnrichment?>(
            new RecipeDishEnrichment(FulfillmentPercent: 75, TotalCost: 8.50m, CostIsPartial: false, HasExpiringIngredients: false));
    }

    public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid recipeId, int servings, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeMissingIngredient>>([]);
}
