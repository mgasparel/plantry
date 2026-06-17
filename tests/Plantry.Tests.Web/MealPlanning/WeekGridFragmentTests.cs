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
        // CSS class used by the existing smoke test
        Assert.Contains("plan-grid__slot-label", html);
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

    // ── Editor fragment ───────────────────────────────────────────────────────

    [Fact(DisplayName = "GET /MealPlan?handler=Editor returns editor partial for a known slot")]
    public async Task Get_EditorHandler_Returns_Editor_Partial()
    {
        var client = CreateClient();
        AddHouseholdHeader(client);

        // Use the first active slot (Breakfast) from the shared fixture config
        var slotId = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First().Id.Value;
        var date = "2026-06-01"; // A Monday

        var response = await client.GetAsync($"/MealPlan?handler=Editor&date={date}&slotId={slotId:D}");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("ed-head", html);
        Assert.Contains("Breakfast", html);
    }

    [Fact(DisplayName = "GET /MealPlan?handler=Editor returns 404 for unknown slotId")]
    public async Task Get_EditorHandler_Returns_404_ForUnknownSlot()
    {
        var client = CreateClient();
        AddHouseholdHeader(client);

        var unknownSlotId = Guid.NewGuid();
        var response = await client.GetAsync($"/MealPlan?handler=Editor&date=2026-06-01&slotId={unknownSlotId:D}");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
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

    // ── Search fragment ───────────────────────────────────────────────────────

    [Fact(DisplayName = "GET /MealPlan?handler=Search returns dish results partial")]
    public async Task Get_SearchHandler_Returns_Dish_Results()
    {
        var client = CreateClient();
        AddHouseholdHeader(client);

        var response = await client.GetAsync("/MealPlan?handler=Search&q=pasta");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Pasta Bolognese", html);
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
// (the dishKinds/dishItemIds/dishServings index-aligned form arrays are parsed
// correctly by BuildDishSpecs, regardless of the order kinds appear).
//
// Uses a capturing WAF variant with a CapturingMealPlanRepo so we can inspect
// what DishSpecs were passed through to AssignDishesAsync.

[Collection(nameof(DishServingsCollection))]
public sealed class DishServingsAlignmentTests(DishServingsFactory factory)
{
    [Fact(DisplayName = "POST Assign with mixed recipe+product dishes preserves per-dish servings")]
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

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            new KeyValuePair<string, string>("mode", "dishes"),
            // dish 0: recipe, 3 servings
            new KeyValuePair<string, string>("dishKinds", "recipe"),
            new KeyValuePair<string, string>("dishItemIds", recipeId.ToString("D")),
            new KeyValuePair<string, string>("dishServings", "3"),
            // dish 1: product, 7 servings
            new KeyValuePair<string, string>("dishKinds", "product"),
            new KeyValuePair<string, string>("dishItemIds", productId.ToString("D")),
            new KeyValuePair<string, string>("dishServings", "7"),
        });

        var response = await client.PostAsync($"/MealPlan?handler=Assign&date=2026-06-01&slotId={slot.Id.Value:D}", form);
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

    public Task SwapMealPositionsAsync(
        PlannedMealId mealAId, DateOnly newDateA, MealSlotId newSlotA,
        PlannedMealId mealBId, DateOnly newDateB, MealSlotId newSlotB,
        Guid updatedBy, DateTimeOffset now,
        CancellationToken ct = default) => Task.CompletedTask;
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
        });
    }
}

/// <summary>No-slots variant: overrides NoSlots to return null config (empty state test).</summary>
public sealed class WeekGridNoSlotsFragmentFactory : WeekGridFragmentFactory
{
    protected override bool NoSlots => true;
}

// ── WAF test doubles ──────────────────────────────────────────────────────────

internal sealed class FakeMealPlanRepo : IMealPlanRepository
{
    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
        => Task.FromResult<MealPlan?>(null);

    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default)
        => Task.FromResult(MealPlan.Start(householdId, weekStart, clock));

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task SwapMealPositionsAsync(
        PlannedMealId mealAId, DateOnly newDateA, MealSlotId newSlotA,
        PlannedMealId mealBId, DateOnly newDateB, MealSlotId newSlotB,
        Guid updatedBy, DateTimeOffset now,
        CancellationToken ct = default) => Task.CompletedTask;
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
    [Fact(DisplayName = "POST Assign with Restricted-tag recipe includes ed-warn in cell fragment")]
    public async Task Assign_WithRestrictedTag_CellFragment_Contains_EdWarn()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HardStanceWarningFixture.HouseholdId.ToString());

        var slot = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();
        var recipeId = HardStanceWarningFixture.RestrictedRecipeId;

        // GET the page first to obtain the antiforgery token + paired cookie
        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            new KeyValuePair<string, string>("mode", "dishes"),
            new KeyValuePair<string, string>("dishKinds", "recipe"),
            new KeyValuePair<string, string>("dishItemIds", recipeId.ToString("D")),
            new KeyValuePair<string, string>("dishServings", "2"),
            // Force the attendee with the Restricted preference into the effective attendees
            new KeyValuePair<string, string>("attendeesOverride", HardStanceWarningFixture.AttendeeUserId.ToString("D")),
            new KeyValuePair<string, string>("attendeesOverridden", "true"),
        });

        var response = await client.PostAsync($"/MealPlan?handler=Assign&date=2026-06-01&slotId={slot.Id.Value:D}", form);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // The cell fragment must include the dietary warning banner
        Assert.Contains("ed-warn", html);
        Assert.Contains("Restricted", html);
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
