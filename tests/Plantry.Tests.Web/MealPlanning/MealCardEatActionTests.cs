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
/// L4 fragment tests for the product-dish "Eat"/Undo action (plantry-zcbx): handler wiring (the real
/// port under test is faked, since the write mechanics themselves are proven at L3 by
/// <c>MealPlanEatWriterAdapterTests</c>), auth/tenancy, and the cell-fragment swap. Reuses the shared
/// null test doubles already defined for the MealPlanning fragment suites (<c>WeekGridFragmentTests</c>
/// / <c>ConflictCellFragmentTests</c>) via internal visibility within this namespace.
/// </summary>
public sealed class MealCardEatActionTests
{
    private static string ExtractAntiforgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the page.");
        return match.Groups[1].Value;
    }

    private static FormUrlEncodedContent AntiforgeryForm(string token) => new(
        [new KeyValuePair<string, string>("__RequestVerificationToken", token)]);

    [Fact(DisplayName = "POST /MealPlan?handler=Eat: consumes the product dish and the cell swap shows the Eaten row with Undo")]
    public async Task Eat_Consumes_Dish_And_Swaps_To_Eaten_Row_With_Undo()
    {
        await using var factory = new EatActionFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, EatActionFixture.HouseholdId.ToString());

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var response = await client.PostAsync(
            $"/MealPlan?handler=Eat&plannedDishId={factory.Repo.ProductDishId:D}" +
            $"&date={factory.Repo.TodayIso}&slotId={EatActionFixture.LunchSlotId.Value:D}",
            AntiforgeryForm(token));

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // The write port was invoked with the dish's own identity and servings — not the meal's or a
        // hard-coded value — proving the handler resolved the real planned dish before delegating.
        var call = Assert.Single(factory.Writer.EatCalls);
        Assert.Equal(factory.Repo.ProductDishId, call.DishId);
        Assert.Equal(factory.Repo.ProductId, call.ProductId);
        Assert.Equal(2m, call.Quantity);

        // Cell-fragment swap, not a full page: just the cell (+OOB rail/plan-bar), same shape as
        // every other cell-targeted POST handler in this file.
        Assert.DoesNotContain("<html", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mc-cook-done", html);
        Assert.Contains("Eaten · 2", html);
        Assert.Contains("class=\"undo\"", html); // today's eaten product row carries Undo, not a timestamp
    }

    [Fact(DisplayName = "POST /MealPlan?handler=UndoEat: reverses the eat and the cell swap shows the pending Eat button again")]
    public async Task Undo_Reverses_Eat_And_Swaps_Back_To_Pending()
    {
        await using var factory = new EatActionFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, EatActionFixture.HouseholdId.ToString());

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var eatUrl = $"/MealPlan?handler=Eat&plannedDishId={factory.Repo.ProductDishId:D}" +
                     $"&date={factory.Repo.TodayIso}&slotId={EatActionFixture.LunchSlotId.Value:D}";
        (await client.PostAsync(eatUrl, AntiforgeryForm(token))).EnsureSuccessStatusCode();

        var undoResponse = await client.PostAsync(
            $"/MealPlan?handler=UndoEat&plannedDishId={factory.Repo.ProductDishId:D}" +
            $"&date={factory.Repo.TodayIso}&slotId={EatActionFixture.LunchSlotId.Value:D}",
            AntiforgeryForm(token));
        undoResponse.EnsureSuccessStatusCode();
        var html = await undoResponse.Content.ReadAsStringAsync();

        var undoCall = Assert.Single(factory.Writer.UndoCalls);
        Assert.Equal(factory.Repo.ProductDishId, undoCall.DishId);

        // Back to a pending Eat button — the strip's derived state re-reads the (now empty) status map.
        Assert.Contains("mc-cook-act eat", html);
        Assert.DoesNotContain("mc-cook-done", html);
    }

    [Fact(DisplayName = "POST /MealPlan?handler=Eat: unauthenticated request is rejected (401), never reaches the write port")]
    public async Task Eat_Without_Auth_Is_Rejected_And_Never_Calls_The_Writer()
    {
        await using var factory = new EatActionFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        // No TestAuthHandler.HouseholdHeader — [Authorize] on IndexModel challenges.

        var response = await client.PostAsync(
            $"/MealPlan?handler=Eat&plannedDishId={factory.Repo.ProductDishId:D}" +
            $"&date={factory.Repo.TodayIso}&slotId={EatActionFixture.LunchSlotId.Value:D}",
            content: null);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.Writer.EatCalls);
    }

    [Fact(DisplayName = "POST /MealPlan?handler=Eat: a dish id from another household resolves to nothing (BadRequest), never calls the write port")]
    public async Task Eat_For_Foreign_Household_Dish_Is_Rejected()
    {
        await using var factory = new EatActionFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        // Authenticated, but as a DIFFERENT household than the one the fixture's plan belongs to — the
        // repo's RLS-scoped FindByWeekAsync returns no plan for this household, so the dish cannot
        // resolve through MealsByCell (the tenancy boundary every other cell handler relies on).
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, Guid.NewGuid().ToString());

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var response = await client.PostAsync(
            $"/MealPlan?handler=Eat&plannedDishId={factory.Repo.ProductDishId:D}" +
            $"&date={factory.Repo.TodayIso}&slotId={EatActionFixture.LunchSlotId.Value:D}",
            AntiforgeryForm(token));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Writer.EatCalls);
    }
}

// ── Fixture ─────────────────────────────────────────────────────────────────────

/// <summary>WAF factory wiring a spy <see cref="IMealPlanEatWriter"/> (doubling as the cook-status reader so a
/// POST's effect is visible on the very next cell re-render) and a one-product-dish meal dated today.</summary>
public sealed class EatActionFactory : WebApplicationFactory<Program>
{
    public EatActionMealPlanRepo Repo { get; } = new();
    public SpyEatWriter Writer { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.AddFakeDisplayCurrency("USD");
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
                new FakeUserManager(new AppUser { Id = "00000000-0000-0000-0000-0000000000cc" }));

            services.RemoveAll<IMealPlanRepository>();
            services.AddSingleton<IMealPlanRepository>(Repo);

            services.RemoveAll<IMealSlotConfigRepository>();
            services.AddScoped<IMealSlotConfigRepository>(_ => new FakeSlotRepo(EatActionFixture.SlotConfig));

            services.RemoveAll<IHouseholdMemberReader>();
            services.AddSingleton<IHouseholdMemberReader>(new FakeMemberReader([]));

            services.RemoveAll<IRecipeReadModel>();
            services.AddSingleton<IRecipeReadModel>(new FakeRecipeReader([]));

            services.RemoveAll<IMealPlanWeekReadModel>();
            services.AddSingleton<IMealPlanWeekReadModel>(new NullWeekReadModel());

            services.RemoveAll<IMealPlanCatalogProductReader>();
            services.AddSingleton<IMealPlanCatalogProductReader>(new FakeCatalogProductReaderW(existsResult: true));

            // The port under test's handler wiring: a spy that both records calls AND drives the
            // cook-status reader, so a POST's effect is immediately visible in the next fragment render.
            services.RemoveAll<IMealPlanEatWriter>();
            services.AddSingleton<IMealPlanEatWriter>(Writer);
            services.RemoveAll<IMealPlanCookStatusReader>();
            services.AddSingleton<IMealPlanCookStatusReader>(Writer);

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
}

internal static class EatActionFixture
{
    public static readonly Guid HouseholdId = Guid.Parse("66666666-0000-0000-0000-000000000006");

    private static readonly HouseholdId HhId = SharedKernel.HouseholdId.From(HouseholdId);
    public static readonly MealSlotConfig SlotConfig = MealSlotConfig.CreateWithDefaults(HhId, SharedSystemClock.Instance);

    private static readonly List<MealSlot> OrderedSlots = [.. SlotConfig.Slots.OrderBy(s => s.Ordinal)];
    public static readonly MealSlotId LunchSlotId = OrderedSlots[1].Id;
}

/// <summary>
/// Meal plan repo backing the Eat-action scenario: one product dish (servings=2), dated today, in the
/// Lunch slot. <see cref="FindByWeekAsync"/> is genuinely household-scoped (unlike the cook-strip
/// fixture's single-household shortcut) so the cross-tenant rejection test is real, not assumed.
/// </summary>
public sealed class EatActionMealPlanRepo : IMealPlanRepository
{
    private readonly HouseholdId _household = SharedKernel.HouseholdId.From(EatActionFixture.HouseholdId);
    public MealPlan Plan { get; }
    public DateOnly WeekMonday { get; }
    public DateOnly Today { get; }
    public string TodayIso => Today.ToString("yyyy-MM-dd");

    public Guid ProductId { get; } = Guid.CreateVersion7();
    public Guid ProductDishId { get; private set; }

    public EatActionMealPlanRepo()
    {
        var clock = SharedSystemClock.Instance;
        Today = DateOnly.FromDateTime(DateTime.Today);
        WeekMonday = MealPlan.NormalizeToMonday(Today);

        Plan = MealPlan.Start(_household, WeekMonday, clock);
        Plan.AssignMeal(Today, EatActionFixture.LunchSlotId,
            [new DishSpec(DishKind.Product, ProductId, 2)],
            null, "manual", Guid.Empty, clock);

        var meal = Plan.PlannedMeals.Single(m => m.MealSlotId == EatActionFixture.LunchSlotId);
        ProductDishId = meal.PlannedDishes.Single(d => d.ProductId == ProductId).Id.Value;
    }

    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default) =>
        Task.FromResult(householdId == _household && weekStart == WeekMonday ? Plan : null);

    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default) =>
        Task.FromResult(Plan);

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Doubles as both the write port under test and the cook-status reader, so an Eat/Undo call's effect
/// is visible the moment the handler re-renders the cell — without a real Inventory/Recipes database.
/// </summary>
public sealed class SpyEatWriter : IMealPlanEatWriter, IMealPlanCookStatusReader
{
    private readonly Dictionary<Guid, DishCookStatus> _statuses = new();

    public List<(Guid DishId, Guid ProductId, decimal Quantity, Guid UserId)> EatCalls { get; } = [];
    public List<(Guid DishId, Guid ProductId, decimal Quantity, Guid UserId)> UndoCalls { get; } = [];

    public Task EatAsync(Guid plannedDishId, Guid productId, decimal quantity, Guid userId, CancellationToken ct = default)
    {
        EatCalls.Add((plannedDishId, productId, quantity, userId));
        _statuses[plannedDishId] = new DishCookStatus(DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    public Task UndoEatAsync(Guid plannedDishId, Guid productId, decimal quantity, Guid userId, CancellationToken ct = default)
    {
        UndoCalls.Add((plannedDishId, productId, quantity, userId));
        _statuses.Remove(plannedDishId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<Guid, DishCookStatus>> GetStatusesAsync(
        IReadOnlyCollection<Guid> plannedDishIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, DishCookStatus> result = plannedDishIds
            .Where(_statuses.ContainsKey)
            .ToDictionary(id => id, id => _statuses[id]);
        return Task.FromResult(result);
    }
}
