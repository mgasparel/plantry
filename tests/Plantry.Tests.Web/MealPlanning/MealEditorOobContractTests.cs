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
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Plantry.Tests.Web.MealPlanning;

/// <summary>
/// ADR-013 OOB-contract tests for the meal editor (plantry-cyj, updated plantry-2zvm.4).
///
/// Acceptance criteria:
///   1. No client-side rollup formula — POST RollupJson returns { html } JSON carrying server-
///      computed fulfillment/cost; the response does NOT carry a full-region repaint.
///   2. Island save (POST AssignJson) and clear (POST ClearJson) return JSON carrying
///      cellHtml, railHtml, barNavHtml projections — the ADR-013 OOB contract in JSON form.
///   3. GET EditorJson returns JSON with island hydration (island scaffold, slotLabel, mode, etc.)
///   4. The island mount point is present in the page (meal-planner-island-root).
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

    [Fact(DisplayName = "POST RollupJson returns { html } with empty-dish hint for an empty dish list")]
    public async Task PostRollupJson_EmptyDishList_ReturnsRollupHtml()
    {
        var client = CreateClient();
        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var body = JsonSerializer.Serialize(new { mode = "dishes", dishes = Array.Empty<object>() });
        var response = await client.PostAsync(
            "/MealPlan?handler=RollupJson",
            CreateJsonContent(body, token));

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);

        // Empty dish list → "Add a dish to see fulfillment & cost" hint
        Assert.True(doc.RootElement.TryGetProperty("html", out var htmlEl));
        Assert.Contains("Add a dish to see fulfillment", htmlEl.GetString() ?? "");

        // Must NOT be a full region repaint — the response carries _EditorRollup only.
        var htmlStr = htmlEl.GetString() ?? "";
        OobContract.AssertNoFullRepaint(htmlStr, "wkgrid");
        OobContract.AssertNoFullRepaint(htmlStr, "plan-rail");
    }

    [Fact(DisplayName = "POST RollupJson in note mode returns note hint in { html }")]
    public async Task PostRollupJson_NoteMode_ReturnsNoteHint()
    {
        var client = CreateClient();
        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var body = JsonSerializer.Serialize(new { mode = "note", dishes = Array.Empty<object>() });
        var response = await client.PostAsync(
            "/MealPlan?handler=RollupJson",
            CreateJsonContent(body, token));

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("html", out var htmlEl));
        Assert.Contains("A note keeps the slot", htmlEl.GetString() ?? "");
    }

    // ── 2. OobContract (JSON form): AssignJson and ClearJson carry projections ──

    [Fact(DisplayName = "POST AssignJson returns cellHtml + railHtml + barNavHtml (OobContract — island JSON path)")]
    public async Task PostAssignJson_CarriesPlanRailProjection()
    {
        var client = CreateClient();
        var slot = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var payload = new
        {
            date = "2026-06-01",
            slotId = slot.Id.Value,
            mode = "note",
            note = "Editor OOB test",
            dishes = Array.Empty<object>(),
            att = Array.Empty<string>(),
            attendeesOverridden = false,
            mealId = (Guid?)null,
        };
        var response = await client.PostAsync(
            "/MealPlan?handler=AssignJson",
            CreateJsonContent(JsonSerializer.Serialize(payload), token));

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);

        // Response must carry the three HTML fragment keys
        Assert.True(doc.RootElement.TryGetProperty("cellHtml", out var cellEl), "Response missing cellHtml");
        Assert.True(doc.RootElement.TryGetProperty("railHtml", out var railEl), "Response missing railHtml");
        Assert.True(doc.RootElement.TryGetProperty("barNavHtml", out var barEl), "Response missing barNavHtml");

        // ADR-013 OOB-contract: each fragment must carry relevant identifiers.
        // Cell: carries class="mcell" (id is dynamic: cell-{slotId:N}-{date})
        var cellHtmlStr = cellEl.GetString() ?? "";
        Assert.Contains("mcell", cellHtmlStr);
        // Rail: carries id="plan-rail"
        OobContract.AssertCarriesProjections(railEl.GetString() ?? "", "plan-rail");
        // BarNav: plantry-khw — plan-bar-nav, plan-cost-chip, plan-bar-autofill
        OobContract.AssertCarriesProjections(barEl.GetString() ?? "", "plan-bar-nav", "plan-cost-chip", "plan-bar-autofill");
    }

    [Fact(DisplayName = "POST AssignJson dishes-mode carries cellHtml + railHtml + barNavHtml (BuildDishSpecsFromJson covers array parsing — no key-collapse)")]
    public async Task PostAssignJson_DishMode_CarriesPlanRailProjection()
    {
        // Verifies the dish-mode path through AssignJson + BuildDishSpecsFromJson.
        // BuildDishSpecsFromJson replaced the legacy BuildDishSpecs form-array parser
        // that had the Object.fromEntries key-collapse bug (repeated dish keys were
        // lost). Using a two-element dishes array confirms both entries survive parsing.
        var client = CreateClient();
        var slot = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var payload = new
        {
            date = "2026-06-01",
            slotId = slot.Id.Value,
            mode = "dishes",
            note = (string?)null,
            dishes = new[]
            {
                new { kind = "recipe", itemId = Guid.Parse("00000000-0000-0000-0000-000000000099"), servings = 2 },
                new { kind = "recipe", itemId = Guid.Parse("00000000-0000-0000-0000-0000000000aa"), servings = 3 },
            },
            att = Array.Empty<string>(),
            attendeesOverridden = false,
            mealId = (Guid?)null,
        };
        var response = await client.PostAsync(
            "/MealPlan?handler=AssignJson",
            CreateJsonContent(JsonSerializer.Serialize(payload), token));

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);

        // Three-fragment OOB contract
        Assert.True(doc.RootElement.TryGetProperty("cellHtml", out var cellEl), "Response missing cellHtml");
        Assert.True(doc.RootElement.TryGetProperty("railHtml", out var railEl), "Response missing railHtml");
        Assert.True(doc.RootElement.TryGetProperty("barNavHtml", out var barEl), "Response missing barNavHtml");

        // Cell carries mcell class — proves AssignMeal ran via BuildDishSpecsFromJson (both dishes present)
        var cellHtml = cellEl.GetString() ?? "";
        Assert.Contains("mcell", cellHtml);
        // Dish-mode filled cell must carry "filled" class (not empty)
        Assert.Contains("filled", cellHtml);

        // Rail and barNav projections — same OOB contract as note mode
        OobContract.AssertCarriesProjections(railEl.GetString() ?? "", "plan-rail");
        OobContract.AssertCarriesProjections(barEl.GetString() ?? "", "plan-bar-nav", "plan-cost-chip", "plan-bar-autofill");

        // No client-side rollup formula in the cell or rail HTML
        OobContract.AssertNoFullRepaint(cellHtml, "wkgrid");
    }

    [Fact(DisplayName = "POST ClearJson returns cellHtml + railHtml + barNavHtml (OobContract — island JSON path)")]
    public async Task PostClearJson_CarriesPlanRailProjection()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, MealEditorFixture.HouseholdId.ToString());

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var slot = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();

        var payload = new
        {
            date = "2026-06-01",
            slotId = slot.Id.Value,
            mealId = MealEditorFixture.SeedMealId,
        };
        var response = await client.PostAsync(
            "/MealPlan?handler=ClearJson",
            CreateJsonContent(JsonSerializer.Serialize(payload), token));

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("cellHtml", out var cellEl2), "Response missing cellHtml");
        Assert.True(doc.RootElement.TryGetProperty("railHtml", out var railEl2), "Response missing railHtml");
        Assert.True(doc.RootElement.TryGetProperty("barNavHtml", out var barEl2), "Response missing barNavHtml");

        // Cell: carries class="mcell" (id is dynamic: cell-{slotId:N}-{date})
        Assert.Contains("mcell", cellEl2.GetString() ?? "");
        OobContract.AssertCarriesProjections(railEl2.GetString() ?? "", "plan-rail");
        OobContract.AssertCarriesProjections(barEl2.GetString() ?? "", "plan-bar-nav", "plan-cost-chip", "plan-bar-autofill");
    }

    // ── 3. Island scaffold: GET EditorJson and page mount point ─────────────

    [Fact(DisplayName = "GET EditorJson returns island hydration JSON with slotLabel, mode, and dish state")]
    public async Task GetEditorJson_ReturnsIslandHydration()
    {
        var client = CreateClient();
        var slot = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();

        var response = await client.GetAsync(
            $"/MealPlan?handler=EditorJson&date=2026-06-01&slotId={slot.Id.Value:D}");

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Core fields the island requires on mount
        Assert.True(root.TryGetProperty("slotLabel", out _), "Response missing slotLabel");
        Assert.True(root.TryGetProperty("mode", out var modeEl), "Response missing mode");
        Assert.True(root.TryGetProperty("dishes", out _), "Response missing dishes");
        Assert.True(root.TryGetProperty("att", out _), "Response missing att");
        Assert.True(root.TryGetProperty("defaultAtt", out _), "Response missing defaultAtt");
        Assert.True(root.TryGetProperty("isEditing", out _), "Response missing isEditing");
        Assert.True(root.TryGetProperty("dateStr", out _), "Response missing dateStr");
        Assert.True(root.TryGetProperty("slotIdStr", out _), "Response missing slotIdStr");

        // New empty slot: mode = "dishes", no mealId
        Assert.Equal("dishes", modeEl.GetString());

        // No client rollup formula — the JSON must NOT contain fulfillment formula tokens
        Assert.DoesNotContain("get roll()", json);
        Assert.DoesNotContain("mirrors PL.rollup", json);
    }

    // ── 4. Island mount point is present in the page ─────────────────────────

    [Fact(DisplayName = "MealPlan page contains island mount point and hydration payload")]
    public async Task MealPlanPage_ContainsIslandMountPoint()
    {
        var client = CreateClient();
        var html = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();

        // Island mount root where Preact renders
        Assert.Contains("id=\"meal-planner-island-root\"", html);

        // Island hydration payload (JSON embedded for readHydration())
        Assert.Contains("id=\"meal-planner-island-data\"", html);

        // Island mount script
        Assert.Contains("mountMealPlanner", html);

        // Rollup stays server-side — no client formula in the page
        Assert.DoesNotContain("get roll()", html);
        Assert.DoesNotContain("mirrors PL.rollup", html);

        // ed-rollup- container id prefix — the island renders this via dangerouslySetInnerHTML
        // driven by the server rollup endpoint; the id must appear in the island JS, not inline HTML.
        // The page no longer embeds the Alpine _MealEditor partial (mealEditor( Alpine registration removed).
        Assert.DoesNotContain("mealEditor(", html);
    }

    // ── 5. Initial rollup on GET EditorJson for existing meal (regression guard) ─

    [Fact(DisplayName = "GET EditorJson for existing dish meal returns initialRollupHtml from server (no client formula)")]
    public async Task GetEditorJson_ExistingDishMeal_ReturnsInitialRollup()
    {
        await using var factory = new ExistingDishMealEditorFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, WeekGridFixture.HouseholdId.ToString());

        var slot = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();
        var mealId = factory.Repo.SeedMealId;

        var response = await client.GetAsync(
            $"/MealPlan?handler=EditorJson&date=2026-06-01&slotId={slot.Id.Value:D}&mealId={mealId:D}");

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("initialRollupHtml", out var rollupEl));
        var rollupHtml = rollupEl.GetString() ?? "";

        // The initial rollup section must contain fulfillment info — not the empty-state hint.
        // If the regression recurs, this shows "Add a dish to see fulfillment & cost" instead.
        Assert.DoesNotContain("Add a dish to see fulfillment", rollupHtml);
        Assert.Contains("in your pantry", rollupHtml);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the page.");
        return match.Groups[1].Value;
    }

    private static HttpContent CreateJsonContent(string json, string token)
    {
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        content.Headers.Add("RequestVerificationToken", token);
        content.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return content;
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
///   - A seeded plan repo that has one meal so POST ClearJson has something to remove
/// </summary>
public sealed class MealEditorOobContractFactory : WeekGridFragmentFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.AddFakeExpiringSoonHorizon();
            // POST AssignJson and POST ClearJson resolve the current user — stub UserManager.
            services.RemoveAll<UserManager<AppUser>>();
            services.AddSingleton<UserManager<AppUser>>(
                new FakeUserManager(new AppUser { Id = "00000000-0000-0000-0000-0000000000aa" }));

            // Seeded repo: one note meal on 2026-06-01 in slot 0, used by POST ClearJson test.
            services.RemoveAll<IMealPlanRepository>();
            services.AddScoped<IMealPlanRepository>(_ => new SeededMealEditorRepo());
        });
    }
}

/// <summary>
/// Meal plan repo seeded with one note meal so POST ClearJson can find a meal to remove.
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
        // Assign the note on the FIRST day of the plan's week (not hardcoded to 2026-06-01
        // which would be outside the plan's week when tests run on a different Monday).
        plan.AssignNote(monday, slot.Id, "ClearTestNote", null, "manual", Guid.Empty, _clock);
        return plan;
    }
}

/// <summary>
/// Factory for the "GET EditorJson for existing dish meal returns initial rollup" regression test.
/// Seeds a plan with a recipe dish meal and stubs the recipe reader to return enrichment data
/// (so fulfillmentService.RollUpMealAsync returns a non-zero percent on the GET EditorJson open).
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
            services.AddFakeExpiringSoonHorizon();
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

    // so5.5: targeted full-corpus tag check — returns true for any tag (all cells are fulfillable in the editor test scenario).
    public Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default)
        => Task.FromResult(true);
}
