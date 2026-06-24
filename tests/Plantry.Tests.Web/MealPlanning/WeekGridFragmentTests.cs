using System.Text;
using System.Text.Json;
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
/// L4 fragment tests for the /MealPlan week grid page (P3-3).
/// Uses the WAF harness with fake services — no Postgres touched.
/// </summary>
public sealed class WeekGridFragmentTests : IClassFixture<WeekGridFragmentFactory>
{
    private readonly WeekGridFragmentFactory _factory;

    public WeekGridFragmentTests(WeekGridFragmentFactory factory) => _factory = factory;

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static void AddHouseholdHeader(HttpClient client) =>
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, WeekGridFixture.HouseholdId.ToString());

    // ── Full page GET ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "GET /MealPlan returns 200 with wkgrid and slot labels")]
    public async Task Get_MealPlan_Renders_Grid_With_SlotLabels()
    {
        var client = CreateClient();
        AddHouseholdHeader(client);

        var response = await client.GetAsync("/MealPlan");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("wkgrid", html);
        Assert.Contains("Breakfast", html);
        Assert.Contains("Lunch", html);
        Assert.Contains("Dinner", html);
        // CSS class for the slot band header row (was plan-grid__slot-label; renamed to slot-band in plantry-v0r)
        Assert.Contains("slot-band", html);
    }

    [Fact(DisplayName = "GET /MealPlan renders 7 day-head columns")]
    public async Task Get_MealPlan_Renders_Seven_Day_Columns()
    {
        var client = CreateClient();
        AddHouseholdHeader(client);

        var response = await client.GetAsync("/MealPlan");
        var html = await response.Content.ReadAsStringAsync();

        // Count day-head occurrences
        var count = CountOccurrences(html, "day-head");
        Assert.Equal(7, count);
    }

    [Fact(DisplayName = "Unauthenticated GET /MealPlan returns 401")]
    public async Task Unauthenticated_Returns_401()
    {
        var client = CreateClient();
        // No household header

        var response = await client.GetAsync("/MealPlan");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Grid fragment ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "GET /MealPlan?handler=Grid returns the week grid partial")]
    public async Task Get_GridHandler_Returns_WeekGrid_Partial()
    {
        var client = CreateClient();
        AddHouseholdHeader(client);

        var response = await client.GetAsync("/MealPlan?handler=Grid");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("wkgrid", html);
        Assert.Contains("slot-band", html);
    }

    // ── No slots empty state ──────────────────────────────────────────────────

    [Fact(DisplayName = "GET /MealPlan with no slots shows configure message")]
    public async Task Get_MealPlan_WithNoSlots_ShowsEmptyState()
    {
        // Use a separate factory subclass — IClassFixture doesn't support parameterised constructors
        await using var factory = new WeekGridNoSlotsFragmentFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, WeekGridFixture.HouseholdId.ToString());

        var response = await client.GetAsync("/MealPlan");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("No meal slots are configured", html);
        Assert.Contains("Configure meal slots", html);
    }

    // ── MP-O8: multi-meal cell rendering ─────────────────────────────────────

    [Fact(DisplayName = "MP-O8: cell with two stacked meals renders two meal-cards and an add-meal button")]
    public async Task MultiMealCell_Renders_TwoCards_And_AddMealButton()
    {
        // Use a factory variant that returns a plan pre-seeded with 2 meals in one cell
        await using var factory = new MultiMealCellFragmentFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, WeekGridFixture.HouseholdId.ToString());

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Two meal cards must be present (MP-O8 cell stack)
        var cardCount = CountOccurrences(html, "meal-card");
        Assert.True(cardCount >= 2, $"Expected at least 2 meal-card elements, found {cardCount}.");

        // The add-meal button must be present for the filled cell (proto parity)
        Assert.Contains("add-meal", html);

        // Both notes must appear
        Assert.Contains("StackNoteOne", html);
        Assert.Contains("StackNoteTwo", html);
    }

    [Fact(DisplayName = "MP-O8 regression: cell fragment returned after an assign still carries the add-meal button")]
    public async Task AssignToFilledCell_Fragment_Still_Has_AddMealButton()
    {
        // The bug: _WeekGrid rendered an "Add meal" button on a filled cell, but the
        // _MealCell *fragment* (the cellHtml the island swaps back after an assign) did not —
        // so the button vanished after the first add. This drives the cellHtml path directly.
        await using var factory = new MultiMealCellFragmentFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, WeekGridFixture.HouseholdId.ToString());

        // GET the page to obtain the antiforgery token + the seeded cell's date/slot
        // (the seeded cell already renders an add-meal button in the full grid).
        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        // After island port (plantry-2zvm.4): add-meal buttons call openEditor() not hx-get.
        var cell = System.Text.RegularExpressions.Regex.Match(
            pageHtml,
            "class=\"add-meal\"[^>]*onclick=\"[^\"]*openEditor\\('([^']+)',\\s*'([^']+)',\\s*null\\)");
        Assert.True(cell.Success, "Expected an add-meal button with openEditor onclick on the seeded cell.");
        var date = cell.Groups[1].Value;
        var slotId = cell.Groups[2].Value;

        var payload = new { date, slotId, mode = "note", note = "RegressionStackNote" };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        content.Headers.Add("RequestVerificationToken", token);
        content.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var response = await client.PostAsync("/MealPlan?handler=AssignJson", content);
        response.EnsureSuccessStatusCode();
        var cellHtml = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("cellHtml").GetString() ?? "";

        // The returned cell fragment is filled, so it must still offer the add-meal affordance.
        Assert.Contains("meal-card", cellHtml);
        Assert.Contains("add-meal", cellHtml);
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the page.");
        return match.Groups[1].Value;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index++;
        }
        return count;
    }
}

// ── Dish servings alignment test ──────────────────────────────────────────────
// Verifies that mixed recipe+product dish assignments preserve per-dish servings
// (the JSON dishes array posted to ?handler=AssignJson is parsed by
// BuildDishSpecsFromJson, mapping each dish's servings to the right item).
//
// Uses a capturing WAF variant with a CapturingMealPlanRepo so we can inspect
// what DishSpecs were passed through to AssignDishesAsync.

[Collection(nameof(DishServingsCollection))]
public sealed class DishServingsAlignmentTests(DishServingsFactory factory)
{
    [Fact(DisplayName = "POST AssignJson with mixed recipe+product dishes preserves per-dish servings")]
    public async Task Assign_MixedDishes_ServingsAreIndexAligned()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, WeekGridFixture.HouseholdId.ToString());

        var slot = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();
        var recipeId = Guid.NewGuid();
        var productId = Guid.NewGuid();

        // GET the page first to obtain the antiforgery token + paired cookie
        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var payload = new
        {
            date = "2026-06-01",
            slotId = slot.Id.Value,
            mode = "dishes",
            dishes = new[]
            {
                // dish 0: recipe, 3 servings
                new { kind = "recipe", itemId = recipeId, servings = 3 },
                // dish 1: product, 7 servings
                new { kind = "product", itemId = productId, servings = 7 },
            },
        };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        content.Headers.Add("RequestVerificationToken", token);
        content.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var response = await client.PostAsync("/MealPlan?handler=AssignJson", content);
        response.EnsureSuccessStatusCode();

        var stored = factory.CapturingRepo.Stored;
        Assert.NotNull(stored);
        var meal = Assert.Single(stored!.PlannedMeals);
        var dishes = meal.PlannedDishes.OrderBy(d => d.Ordinal).ToList();
        Assert.Equal(2, dishes.Count);

        // dish 0: recipe with 3 servings
        Assert.NotNull(dishes[0].RecipeId);
        Assert.Equal(recipeId, dishes[0].RecipeId!.Value);
        Assert.Equal(3, dishes[0].Servings);

        // dish 1: product with 7 servings
        Assert.NotNull(dishes[1].ProductId);
        Assert.Equal(productId, dishes[1].ProductId!.Value);
        Assert.Equal(7, dishes[1].Servings);
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the page.");
        return match.Groups[1].Value;
    }
}

[CollectionDefinition(nameof(DishServingsCollection))]
public sealed class DishServingsCollection : ICollectionFixture<DishServingsFactory> { }

public sealed class DishServingsFactory : WebApplicationFactory<Program>
{
    public CapturingMealPlanRepo CapturingRepo { get; } = new();

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

            // Stub out UserManager so GetCurrentUserIdAsync doesn't hit a real Identity DB
            services.RemoveAll<UserManager<AppUser>>();
            services.AddSingleton<UserManager<AppUser>>(
                new FakeUserManager(new AppUser { Id = "00000000-0000-0000-0000-0000000000aa" }));

            services.RemoveAll<IMealPlanRepository>();
            services.AddScoped<IMealPlanRepository>(_ => CapturingRepo);

            services.RemoveAll<IMealSlotConfigRepository>();
            services.AddScoped<IMealSlotConfigRepository>(_ => new FakeSlotRepo(WeekGridFixture.SharedConfig));

            services.RemoveAll<IHouseholdMemberReader>();
            services.AddSingleton<IHouseholdMemberReader>(new FakeMemberReader(WeekGridFixture.Members));

            services.RemoveAll<IRecipeReadModel>();
            services.AddSingleton<IRecipeReadModel>(new FakeRecipeReader(WeekGridFixture.Recipes));

            services.RemoveAll<IMealPlanCatalogProductReader>();
            // Use a product reader that reports all products as existing (for AssignDishesAsync catalog check).
            services.AddSingleton<IMealPlanCatalogProductReader>(new FakeCatalogProductReaderW(existsResult: true));

            services.RemoveAll<AssignMealService>();
            services.AddScoped<AssignMealService>();
            services.RemoveAll<MoveMealService>();
            services.AddScoped<MoveMealService>();

            // Stub the P3-4 port interfaces so PlanFulfillmentService / PlanCostingService
            // / ShopForWeekService resolve without real Inventory/Pricing/Shopping infrastructure.
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

            // P3-6a: stub AI planner + proposal store
            services.RemoveAll<IMealPlanner>();
            services.AddSingleton<IMealPlanner>(new NullMealPlanner());
            services.RemoveAll<IPendingProposalStore>();
            services.AddSingleton<IPendingProposalStore>(new NullPendingProposalStore());
            services.RemoveAll<GeneratePlanService>();
            services.AddScoped<GeneratePlanService>();
            services.RemoveAll<AcceptProposalService>();
            services.AddScoped<AcceptProposalService>();

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

public sealed class CapturingMealPlanRepo : IMealPlanRepository
{
    private MealPlan? _stored;
    public MealPlan? Stored => _stored;

    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
        => Task.FromResult(_stored);

    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default)
    {
        _stored ??= MealPlan.Start(householdId, weekStart, clock);
        return Task.FromResult(_stored);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

// Renamed to avoid collision with FakeCatalogProductReader in AssignMealServiceTests.cs
internal sealed class FakeCatalogProductReaderW(bool existsResult = true) : IMealPlanCatalogProductReader
{
    public Task<bool> ExistsAsync(Guid productId, CancellationToken ct = default)
        => Task.FromResult(existsResult);

    public Task<IReadOnlyList<MealPlanProductReadModel>> SearchAsync(string nameQuery, int maxResults, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MealPlanProductReadModel>>([]);

    public Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(IReadOnlyList<Guid> productIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
}

// ── Fixture ───────────────────────────────────────────────────────────────────

public static class WeekGridFixture
{
    public static readonly Guid HouseholdId = Guid.Parse("11111111-0000-0000-0000-000000000001");

    private static readonly HouseholdId HhId = Plantry.SharedKernel.HouseholdId.From(HouseholdId);
    private static readonly IClock Clock = Plantry.SharedKernel.Domain.SystemClock.Instance;

    /// <summary>Shared singleton config so slot IDs are stable within a test run.</summary>
    public static readonly MealSlotConfig SharedConfig = MealSlotConfig.CreateWithDefaults(HhId, Clock);

    public static IReadOnlyList<HouseholdMember> Members =>
        [new HouseholdMember(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"), "Alice", "A")];

    public static IReadOnlyList<RecipeReadModel> Recipes =>
        [new RecipeReadModel(Guid.NewGuid(), "Pasta Bolognese", [], 4)];

    public static IReadOnlyList<MealPlanProductReadModel> Products => [];
}

// ── Factory ───────────────────────────────────────────────────────────────────

public class WeekGridFragmentFactory : WebApplicationFactory<Program>
{
    /// <summary>Override to true to return no slots from the fake repo (tests empty state).</summary>
    protected virtual bool NoSlots => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // Auth: header-driven test scheme
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Replace MealPlan repository
            services.RemoveAll<IMealPlanRepository>();
            services.AddScoped<IMealPlanRepository>(_ => new FakeMealPlanRepo());

            // Replace slot config repo (use shared config for stable slot IDs)
            var config = NoSlots ? null : WeekGridFixture.SharedConfig;
            services.RemoveAll<IMealSlotConfigRepository>();
            services.AddScoped<IMealSlotConfigRepository>(_ => new FakeSlotRepo(config));

            // Replace household member reader
            services.RemoveAll<IHouseholdMemberReader>();
            services.AddSingleton<IHouseholdMemberReader>(new FakeMemberReader(WeekGridFixture.Members));

            // Replace recipe and catalog readers
            services.RemoveAll<IRecipeReadModel>();
            services.AddSingleton<IRecipeReadModel>(new FakeRecipeReader(WeekGridFixture.Recipes));

            services.RemoveAll<IMealPlanCatalogProductReader>();
            services.AddSingleton<IMealPlanCatalogProductReader>(new FakeProductReader(WeekGridFixture.Products));

            // Re-register services that depend on the fakes
            services.RemoveAll<AssignMealService>();
            services.AddScoped<AssignMealService>();
            services.RemoveAll<MoveMealService>();
            services.AddScoped<MoveMealService>();

            // Stub the P3-4 port interfaces so PlanFulfillmentService / PlanCostingService
            // / ShopForWeekService resolve without real Inventory/Pricing/Shopping infrastructure.
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

            // P3-6a: stub AI planner, proposal store, and application services
            services.RemoveAll<IMealPlanner>();
            services.AddSingleton<IMealPlanner>(new NullMealPlanner());
            services.RemoveAll<IPendingProposalStore>();
            services.AddSingleton<IPendingProposalStore>(new NullPendingProposalStore());
            services.RemoveAll<GeneratePlanService>();
            services.AddScoped<GeneratePlanService>();
            services.RemoveAll<AcceptProposalService>();
            services.AddScoped<AcceptProposalService>();

            // Stub UserPreferences (needed by AcceptProposalService / GeneratePlanService)
            services.RemoveAll<IUserPreferenceRepository>();
            services.AddSingleton<IUserPreferenceRepository>(new NullPrefsRepo());

            // Stub ITagReader (needed by GeneratePlanService for unfulfillable tag name resolution)
            services.RemoveAll<ITagReader>();
            services.AddSingleton<ITagReader>(new NullTagReader());

            // P3-5: stub expiring-stock reader; re-register insights service
            services.RemoveAll<IMealPlanExpiringStockReader>();
            services.AddSingleton<IMealPlanExpiringStockReader>(new NullExpiringStockReader());
            services.RemoveAll<PlanInsightsService>();
            services.AddScoped<PlanInsightsService>();

            // plantry-so5.3: stub planning settings repos so WAF tests don't need Postgres
            services.RemoveAll<IHouseholdPlanningSettingsRepository>();
            services.AddSingleton<IHouseholdPlanningSettingsRepository>(new NullPlanningSettingsRepo());
            services.RemoveAll<IWeekPlanningOverrideRepository>();
            services.AddSingleton<IWeekPlanningOverrideRepository>(new NullWeekOverrideRepo());
            services.RemoveAll<SetPlanningSettingsService>();
            services.AddScoped<SetPlanningSettingsService>();
        });
    }
}

/// <summary>No-slots variant: overrides NoSlots to return null config (empty state test).</summary>
public sealed class WeekGridNoSlotsFragmentFactory : WeekGridFragmentFactory
{
    protected override bool NoSlots => true;
}

/// <summary>
/// MP-O8: variant that seeds a plan with two stacked meals in the first cell
/// (Monday of the current ISO week, first active slot). Used to verify that
/// the week grid renders two meal-card elements and an add-meal button.
/// </summary>
public sealed class MultiMealCellFragmentFactory : WeekGridFragmentFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            // Swap out the empty FakeMealPlanRepo with a pre-seeded two-meal variant
            services.RemoveAll<IMealPlanRepository>();
            services.AddScoped<IMealPlanRepository>(_ => new TwoMealCellRepo());

            // Stub UserManager so the POST Assign handler's GetCurrentUserIdAsync
            // doesn't reach the real Identity DbContext (Postgres) under test.
            services.RemoveAll<UserManager<AppUser>>();
            services.AddSingleton<UserManager<AppUser>>(
                new FakeUserManager(new AppUser { Id = "00000000-0000-0000-0000-0000000000aa" }));
        });
    }
}

/// <summary>
/// Returns a plan with two note meals in the first active slot on the current Monday.
/// Used by MultiMealCellFragmentFactory to exercise the cell-stack rendering path.
/// </summary>
internal sealed class TwoMealCellRepo : IMealPlanRepository
{
    private static readonly IClock _clock = Plantry.SharedKernel.Domain.SystemClock.Instance;

    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
    {
        var plan = BuildPlan(householdId, weekStart);
        return Task.FromResult<MealPlan?>(plan);
    }

    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default)
    {
        var plan = BuildPlan(householdId, weekStart);
        return Task.FromResult(plan);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

    private static MealPlan BuildPlan(HouseholdId householdId, DateOnly weekStart)
    {
        var monday = MealPlan.NormalizeToMonday(weekStart);
        var slotId = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First().Id;
        var plan = MealPlan.Start(householdId, monday, _clock);
        plan.AssignNote(monday, slotId, "StackNoteOne", null, "manual", Guid.Empty, _clock);
        plan.AssignNote(monday, slotId, "StackNoteTwo", null, "manual", Guid.Empty, _clock);
        return plan;
    }
}

// ── WAF test doubles ──────────────────────────────────────────────────────────

internal sealed class FakeMealPlanRepo : IMealPlanRepository
{
    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
        => Task.FromResult<MealPlan?>(null);

    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default)
        => Task.FromResult(MealPlan.Start(householdId, weekStart, clock));

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakeSlotRepo(MealSlotConfig? config) : IMealSlotConfigRepository
{
    public Task<MealSlotConfig?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        => Task.FromResult(config);

    public Task AddAsync(MealSlotConfig c, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakeMemberReader(IReadOnlyList<HouseholdMember> members) : IHouseholdMemberReader
{
    public Task<IReadOnlyList<HouseholdMember>> ListMembersAsync(CancellationToken ct = default)
        => Task.FromResult(members);
}

internal sealed class FakeRecipeReader(IReadOnlyList<RecipeReadModel> recipes) : IRecipeReadModel
{
    public Task<RecipeReadModel?> GetByIdAsync(Guid recipeId, CancellationToken ct = default)
        => Task.FromResult(recipes.FirstOrDefault(r => r.RecipeId == recipeId));

    public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string nameQuery, int maxResults, CancellationToken ct = default)
    {
        var results = recipes
            .Where(r => r.Name.Contains(nameQuery, StringComparison.OrdinalIgnoreCase))
            .Take(maxResults)
            .ToList();
        return Task.FromResult<IReadOnlyList<RecipeReadModel>>(results);
    }

    public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid recipeId, int servings, DateOnly today, CancellationToken ct = default)
        => Task.FromResult<RecipeDishEnrichment?>(null);

    public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid recipeId, int servings, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeMissingIngredient>>([]);

    /// <summary>
    /// Targeted full-corpus query: returns true when ANY recipe in the in-memory list carries the tag.
    /// Unlike SearchAsync (which respects maxResults), this queries ALL recipes — no cap.
    /// </summary>
    public Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default)
        => Task.FromResult(recipes.Any(r => r.TagIds.Contains(tagId)));
}

internal sealed class FakeProductReader(IReadOnlyList<MealPlanProductReadModel> products) : IMealPlanCatalogProductReader
{
    public Task<bool> ExistsAsync(Guid productId, CancellationToken ct = default)
        => Task.FromResult(products.Any(p => p.ProductId == productId));

    public Task<IReadOnlyList<MealPlanProductReadModel>> SearchAsync(string nameQuery, int maxResults, CancellationToken ct = default)
    {
        var results = products
            .Where(p => p.Name.Contains(nameQuery, StringComparison.OrdinalIgnoreCase))
            .Take(maxResults)
            .ToList();
        return Task.FromResult<IReadOnlyList<MealPlanProductReadModel>>(results);
    }

    public Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, string> result = productIds
            .Where(id => products.Any(p => p.ProductId == id))
            .ToDictionary(id => id, id => products.First(p => p.ProductId == id).Name);
        return Task.FromResult(result);
    }
}

// ── Hard-stance warning surfacing test ───────────────────────────────────────
// Verifies that when AssignDishesAsync produces a HardStanceWarning (a user has
// a Restricted stance on a tag carried by a recipe dish), the cell fragment
// response includes the ed-warn element with the warning text.
//
// Requires a factory that wires up:
//   - A recipe that carries a dietary tag
//   - A UserPreference with Restricted stance on that tag for a default attendee
//   - The AssignMealService and MealConstraintResolver (real, not faked)

[Collection(nameof(HardStanceWarningCollection))]
public sealed class HardStanceWarningSurfacingTests(HardStanceWarningFactory factory)
{
    [Fact(DisplayName = "POST AssignJson with Restricted-tag recipe includes ed-warn in cell fragment")]
    public async Task Assign_WithRestrictedTag_CellFragment_Contains_EdWarn()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HardStanceWarningFixture.HouseholdId.ToString());

        var slot = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();
        var recipeId = HardStanceWarningFixture.RestrictedRecipeId;

        // GET the page first to obtain the antiforgery token + paired cookie
        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var payload = new
        {
            date = "2026-06-01",
            slotId = slot.Id.Value,
            mode = "dishes",
            dishes = new[] { new { kind = "recipe", itemId = recipeId, servings = 2 } },
            // Force the attendee with the Restricted preference into the effective attendees
            att = new[] { HardStanceWarningFixture.AttendeeUserId },
            attendeesOverridden = true,
        };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        content.Headers.Add("RequestVerificationToken", token);
        content.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var response = await client.PostAsync("/MealPlan?handler=AssignJson", content);
        response.EnsureSuccessStatusCode();
        var cellHtml = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("cellHtml").GetString() ?? "";

        // The cell fragment must include the dietary warning banner
        Assert.Contains("ed-warn", cellHtml);
        Assert.Contains("Restricted", cellHtml);
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the page.");
        return match.Groups[1].Value;
    }
}

[CollectionDefinition(nameof(HardStanceWarningCollection))]
public sealed class HardStanceWarningCollection : ICollectionFixture<HardStanceWarningFactory> { }

/// <summary>
/// Factory that wires a recipe with a dietary tag and a UserPreference with Restricted stance
/// so that AssignDishesAsync returns a non-null HardStanceWarning.
/// </summary>
public sealed class HardStanceWarningFactory : WebApplicationFactory<Program>
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

            // Stub out UserManager so GetCurrentUserIdAsync doesn't hit a real Identity DB
            services.RemoveAll<UserManager<AppUser>>();
            services.AddSingleton<UserManager<AppUser>>(
                new FakeUserManager(new AppUser { Id = "00000000-0000-0000-0000-0000000000aa" }));

            services.RemoveAll<IMealPlanRepository>();
            services.AddScoped<IMealPlanRepository>(_ => new FakeMealPlanRepo());

            // Use the shared slot config (stable slot IDs)
            services.RemoveAll<IMealSlotConfigRepository>();
            services.AddScoped<IMealSlotConfigRepository>(_ => new FakeSlotRepo(WeekGridFixture.SharedConfig));

            services.RemoveAll<IHouseholdMemberReader>();
            services.AddSingleton<IHouseholdMemberReader>(new FakeMemberReader(WeekGridFixture.Members));

            // Recipe reader: returns a recipe that carries the restricted dietary tag
            services.RemoveAll<IRecipeReadModel>();
            services.AddSingleton<IRecipeReadModel>(new FakeRecipeReader(HardStanceWarningFixture.Recipes));

            services.RemoveAll<IMealPlanCatalogProductReader>();
            services.AddSingleton<IMealPlanCatalogProductReader>(new FakeProductReader([]));

            // User preference repo: returns a Restricted stance on the dietary tag for the attendee
            services.RemoveAll<IUserPreferenceRepository>();
            services.AddSingleton<IUserPreferenceRepository>(HardStanceWarningFixture.PreferenceRepo);

            // Re-register services (real)
            services.RemoveAll<AssignMealService>();
            services.AddScoped<AssignMealService>();
            services.RemoveAll<MoveMealService>();
            services.AddScoped<MoveMealService>();

            // Stub the P3-4 port interfaces so PlanFulfillmentService / PlanCostingService
            // / ShopForWeekService resolve without real Inventory/Pricing/Shopping infrastructure.
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

            // P3-6a: stub AI planner + proposal store
            services.RemoveAll<IMealPlanner>();
            services.AddSingleton<IMealPlanner>(new NullMealPlanner());
            services.RemoveAll<IPendingProposalStore>();
            services.AddSingleton<IPendingProposalStore>(new NullPendingProposalStore());
            services.RemoveAll<GeneratePlanService>();
            services.AddScoped<GeneratePlanService>();
            services.RemoveAll<AcceptProposalService>();
            services.AddScoped<AcceptProposalService>();

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

/// <summary>
/// Test fixtures for the hard-stance warning test: a recipe with a dietary tag and
/// a user preference with Restricted stance on that tag.
/// </summary>
internal static class HardStanceWarningFixture
{
    public static readonly Guid HouseholdId = WeekGridFixture.HouseholdId;
    public static readonly Guid RestrictedTagId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid RestrictedRecipeId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    public static readonly Guid AttendeeUserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"); // matches WeekGridFixture.Members[0]

    public static IReadOnlyList<RecipeReadModel> Recipes =>
        [new RecipeReadModel(RestrictedRecipeId, "Gluten-containing dish", [RestrictedTagId], 4)];

    public static IUserPreferenceRepository PreferenceRepo { get; } = new HardStancePreferenceRepo();

    private sealed class HardStancePreferenceRepo : IUserPreferenceRepository
    {
        private static readonly UserPreference _pref = BuildPref();

        private static UserPreference BuildPref()
        {
            var hhId = Plantry.SharedKernel.HouseholdId.From(WeekGridFixture.HouseholdId);
            var pref = UserPreference.Create(hhId, AttendeeUserId, Plantry.SharedKernel.Domain.SystemClock.Instance);
            pref.SetStance(RestrictedTagId, "Restricted", Plantry.SharedKernel.Domain.SystemClock.Instance);
            return pref;
        }

        public Task<UserPreference?> FindByUserIdAsync(Guid userId, CancellationToken ct = default)
        {
            var result = userId == AttendeeUserId ? _pref : null;
            return Task.FromResult<UserPreference?>(result);
        }

        public Task AddAsync(UserPreference preference, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}

// ── P3-6a null stubs (no-op implementations for WAF factories that don't test AI generation) ────

internal sealed class NullMealPlanner : IMealPlanner
{
    public Task<IReadOnlyList<ProposedMeal>> ProposeWeekAsync(
        IReadOnlyList<PlannerMealSlotContext> slotsContext,
        PlanningWeights weights,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ProposedMeal>>([]);
}

internal sealed class NullPendingProposalStore : IPendingProposalStore
{
    public Task<IReadOnlyList<ProposedMeal>> GetAsync(string storeKey, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ProposedMeal>>([]);
    public Task SetAsync(string storeKey, IReadOnlyList<ProposedMeal> proposals, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task RemoveAsync(string storeKey, DateOnly date, MealSlotId slotId, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task ClearAsync(string storeKey, CancellationToken ct = default)
        => Task.CompletedTask;
}

internal sealed class NullPrefsRepo : IUserPreferenceRepository
{
    public Task<UserPreference?> FindByUserIdAsync(Guid userId, CancellationToken ct = default)
        => Task.FromResult<UserPreference?>(null);
    public Task AddAsync(UserPreference preference, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

// ── P3-5 null stubs (no-op implementations for WAF factories that don't test insights) ────

internal sealed class NullExpiringStockReader : IMealPlanExpiringStockReader
{
    public Task<IReadOnlyList<Guid>> GetExpiringProductIdsAsync(
        DateOnly today, int withinDays, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Guid>>([]);
}

// ── P3-4 null stubs (no-op implementations for WAF factories that don't test enrichment) ────

internal sealed class NullStockReader : IMealPlanStockReader
{
    public Task<MealPlanProductStock?> FindStockAsync(Guid productId, CancellationToken ct = default)
        => Task.FromResult<MealPlanProductStock?>(null);
}

internal sealed class NullPriceReader : IMealPlanPriceReader
{
    public Task<MealPlanPricePoint?> FindLatestAsync(Guid productId, CancellationToken ct = default)
        => Task.FromResult<MealPlanPricePoint?>(null);
}

internal sealed class NullShoppingWriter : IMealPlanShoppingWriter
{
    public Task AddItemsAsync(IEnumerable<MealPlanShoppingItem> items, string source, Guid sourceRef, CancellationToken ct = default)
        => Task.CompletedTask;
}

// ── plantry-so5.3 null stubs (planning settings — no-op for WAF tests that don't test budget) ──

internal sealed class NullPlanningSettingsRepo : IHouseholdPlanningSettingsRepository
{
    public Task<HouseholdPlanningSettings?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        => Task.FromResult<HouseholdPlanningSettings?>(null);

    public Task AddAsync(HouseholdPlanningSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class NullWeekOverrideRepo : IWeekPlanningOverrideRepository
{
    public Task<WeekPlanningOverride?> FindAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
        => Task.FromResult<WeekPlanningOverride?>(null);

    public Task AddAsync(WeekPlanningOverride weekOverride, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

// NullTagReader is defined in ConflictCellFragmentTests.cs (shared across the MealPlanning test namespace).
