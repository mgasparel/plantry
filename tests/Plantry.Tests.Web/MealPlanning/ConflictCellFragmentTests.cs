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
/// L4 fragment tests for C6 hard-stance conflict cell rendering.
/// Validates that when <see cref="GeneratePlanService"/> detects an irreconcilable
/// hard-stance conflict (two attendees whose Required tags exclude every candidate),
/// the POST /MealPlan?handler=Generate response renders the conflict markers:
///   - <c>class="mcell conflict"</c>
///   - <c>data-conflict="hard-stance"</c>
///   - <c>conflict-notice</c> div with text "No single dish suits everyone here"
/// </summary>
[Collection(nameof(ConflictCellCollection))]
public sealed class ConflictCellFragmentTests(ConflictCellFactory factory)
{
    private HttpClient CreateClient()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, WeekGridFixture.HouseholdId.ToString());
        return client;
    }

    // ── POST Generate returns grid with conflict cell markers ─────────────────

    [Fact(DisplayName = "POST Generate: irreconcilable hard-stance → grid renders mcell conflict class")]
    public async Task PostGenerate_HardConflict_RendersConflictCellClass()
    {
        var client = CreateClient();
        var html = await PostGenerateAndReadGridAsync(client);

        Assert.Contains("mcell conflict", html);
    }

    [Fact(DisplayName = "POST Generate: irreconcilable hard-stance → grid renders data-conflict attribute")]
    public async Task PostGenerate_HardConflict_RendersDataConflictAttribute()
    {
        var client = CreateClient();
        var html = await PostGenerateAndReadGridAsync(client);

        Assert.Contains("data-conflict=\"hard-stance\"", html);
    }

    [Fact(DisplayName = "POST Generate: irreconcilable hard-stance → grid renders conflict-notice with full actionable message")]
    public async Task PostGenerate_HardConflict_RendersConflictNoticeText()
    {
        var client = CreateClient();
        var html = await PostGenerateAndReadGridAsync(client);

        Assert.Contains("conflict-notice", html);
        // so5.5 supersedes so5.4's minimal seed with the full actionable message + dual CTAs.
        Assert.Contains("requirements conflict", html);
    }

    [Fact(DisplayName = "POST Generate: irreconcilable hard-stance → grid renders dual CTAs (add by hand + adjust attendance)")]
    public async Task PostGenerate_HardConflict_RendersDualCtas()
    {
        var client = CreateClient();
        var html = await PostGenerateAndReadGridAsync(client);

        Assert.Contains("conflict-acts", html);
        Assert.Contains("Add a dish by hand", html);
        Assert.Contains("Adjust who", html); // "Adjust who's attending"
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static async Task<string> PostGenerateAndReadGridAsync(HttpClient client)
    {
        // GET the page first to obtain the antiforgery token + paired cookie.
        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var match = System.Text.RegularExpressions.Regex.Match(
            pageHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the page.");
        var token = match.Groups[1].Value;

        var week = ConflictCellFixture.WeekStart.ToString("yyyy-MM-dd");
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });

        var response = await client.PostAsync($"/MealPlan?handler=Generate&week={week}", form);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}

[CollectionDefinition(nameof(ConflictCellCollection))]
public sealed class ConflictCellCollection : ICollectionFixture<ConflictCellFactory> { }

/// <summary>
/// WAF factory that seeds two attendees with mutually exclusive Required stances and a
/// candidate pool where no recipe satisfies both — every cell is irreconcilable (C6).
/// </summary>
public sealed class ConflictCellFactory : WebApplicationFactory<Program>
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

            // Stub UserManager
            services.RemoveAll<UserManager<AppUser>>();
            services.AddSingleton<UserManager<AppUser>>(
                new FakeUserManager(new AppUser { Id = "00000000-0000-0000-0000-0000000000aa" }));

            // Slot config: both Alice and Bob are default attendees on every slot.
            services.RemoveAll<IMealSlotConfigRepository>();
            services.AddScoped<IMealSlotConfigRepository>(_ =>
            {
                var hh = Plantry.SharedKernel.HouseholdId.From(WeekGridFixture.HouseholdId);
                var config = MealSlotConfig.CreateWithDefaults(hh, Plantry.SharedKernel.Domain.SystemClock.Instance);
                foreach (var slot in config.Slots.Where(s => s.IsActive))
                    config.SetDefaultAttendees(slot.Id, [ConflictCellFixture.AliceId, ConflictCellFixture.BobId], Plantry.SharedKernel.Domain.SystemClock.Instance);
                return new FakeSlotRepo(config);
            });

            services.RemoveAll<IHouseholdMemberReader>();
            services.AddSingleton<IHouseholdMemberReader>(
                new FakeMemberReader([
                    new HouseholdMember(ConflictCellFixture.AliceId, "Alice", "A"),
                    new HouseholdMember(ConflictCellFixture.BobId, "Bob", "B"),
                ]));

            // Preferences: Alice requires VeganTag, Bob requires MeatTag.
            services.RemoveAll<IUserPreferenceRepository>();
            services.AddSingleton<IUserPreferenceRepository>(
                new ConflictPrefsRepo(ConflictCellFixture.BuildAlicePref(), ConflictCellFixture.BuildBobPref()));

            // Recipes: one vegan (only VeganTag) + one meat (only MeatTag). No recipe covers both.
            services.RemoveAll<IRecipeReadModel>();
            services.AddSingleton<IRecipeReadModel>(new FakeRecipeReader([
                new RecipeReadModel(ConflictCellFixture.VeganRecipeId, "Vegan Stir-Fry", [ConflictCellFixture.VeganTag], 2),
                new RecipeReadModel(ConflictCellFixture.MeatRecipeId, "Beef Stew", [ConflictCellFixture.MeatTag], 4),
            ]));

            // Tag reader: stub so UnfulfillabilityDetector resolves tag names.
            // Both VeganTag and MeatTag have recipes (so no cell is Unfulfillable — only HardConflict).
            services.RemoveAll<ITagReader>();
            services.AddSingleton<ITagReader>(new NullTagReader());

            services.RemoveAll<IMealPlanRepository>();
            services.AddScoped<IMealPlanRepository>(_ => new FakeMealPlanRepo());

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

            // Planner: NullMealPlanner — never called because all cells conflict.
            services.RemoveAll<IMealPlanner>();
            services.AddSingleton<IMealPlanner>(new NullMealPlanner());
            services.RemoveAll<IPendingProposalStore>();
            services.AddSingleton<IPendingProposalStore>(new NullPendingProposalStore());
            services.RemoveAll<GeneratePlanService>();
            services.AddScoped<GeneratePlanService>();
            services.RemoveAll<AcceptProposalService>();
            services.AddScoped<AcceptProposalService>();

            services.RemoveAll<IUserPreferenceRepository>();
            services.AddSingleton<IUserPreferenceRepository>(
                new ConflictPrefsRepo(ConflictCellFixture.BuildAlicePref(), ConflictCellFixture.BuildBobPref()));

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

// ── ConflictCellFixture ───────────────────────────────────────────────────────

internal static class ConflictCellFixture
{
    /// <summary>Monday of the current ISO week — kept dynamic so dates always fall in the rendered week.</summary>
    public static DateOnly WeekStart
    {
        get
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var offset = ((int)today.DayOfWeek + 6) % 7;
            return today.AddDays(-offset);
        }
    }

    public static readonly Guid AliceId  = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid BobId    = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    public static readonly Guid VeganTag = Guid.Parse("cccccccc-0000-0000-0000-000000000010");
    public static readonly Guid MeatTag  = Guid.Parse("cccccccc-0000-0000-0000-000000000011");

    public static readonly Guid VeganRecipeId = Guid.Parse("dddddddd-0000-0000-0000-000000000020");
    public static readonly Guid MeatRecipeId  = Guid.Parse("dddddddd-0000-0000-0000-000000000021");

    private static readonly HouseholdId Hh =
        Plantry.SharedKernel.HouseholdId.From(WeekGridFixture.HouseholdId);

    public static UserPreference BuildAlicePref()
    {
        var pref = UserPreference.Create(Hh, AliceId, Plantry.SharedKernel.Domain.SystemClock.Instance);
        pref.SetStance(VeganTag, "Required", Plantry.SharedKernel.Domain.SystemClock.Instance);
        return pref;
    }

    public static UserPreference BuildBobPref()
    {
        var pref = UserPreference.Create(Hh, BobId, Plantry.SharedKernel.Domain.SystemClock.Instance);
        pref.SetStance(MeatTag, "Required", Plantry.SharedKernel.Domain.SystemClock.Instance);
        return pref;
    }
}

/// <summary>
/// Prefs repo that returns Alice's or Bob's preferences by their seeded user IDs.
/// </summary>
internal sealed class ConflictPrefsRepo(UserPreference alicePref, UserPreference bobPref) : IUserPreferenceRepository
{
    public Task<UserPreference?> FindByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        if (userId == ConflictCellFixture.AliceId) return Task.FromResult<UserPreference?>(alicePref);
        if (userId == ConflictCellFixture.BobId) return Task.FromResult<UserPreference?>(bobPref);
        return Task.FromResult<UserPreference?>(null);
    }

    public Task AddAsync(UserPreference preference, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>No-op ITagReader stub for WAF tests that don't test tag name resolution.</summary>
internal sealed class NullTagReader : ITagReader
{
    public Task<IReadOnlyList<TagGroup>> ListGroupedAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TagGroup>>([]);
}
