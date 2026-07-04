using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Settings;

/// <summary>
/// L4 fragment tests for the /Settings/MealPlanning household-defaults page (plantry-hv8y).
///
/// Verifies:
///   1. GET /Settings/MealPlanning renders the form — the budget input is populated with the
///      persisted household default budget when one has been set (round-trip assertion).
///   2. GET /Settings/MealPlanning with no defaults set renders the form without error.
///   3. POST ?handler=SetMealPlanningDefaults persists the household default (weekStart: null)
///      and returns the updated form partial with the Saved badge.
///   4. Unauthenticated GET returns 401.
/// </summary>
[Trait("Category", "Web")]
public sealed class MealPlanningSettingsPageTests : IClassFixture<MealPlanningSettingsFactory>
{
    private readonly MealPlanningSettingsFactory _factory;

    public MealPlanningSettingsPageTests(MealPlanningSettingsFactory factory) => _factory = factory;

    // ── 1. Default budget round-trip ─────────────────────────────────────────

    [Fact(DisplayName = "L4: GET /Settings/MealPlanning with seeded default budget populates the budget input")]
    public async Task Get_WithSeededBudget_BudgetInputPopulated()
    {
        await using var factory = new MealPlanningSettingsSeededFactory(budgetDecimal: 150m);
        var client = MakeClient(factory);

        var response = await client.GetAsync("/Settings/MealPlanning");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // The form must be present
        Assert.Contains("meal-planning-defaults-form", html);

        // The rendered budget value from the Alpine x-model initialiser must appear.
        // settingsTune() is called with initialBudget=150 — verifying the server seeds the component.
        Assert.Contains("150", html);
    }

    // ── 2. No defaults set → page renders without error ──────────────────────

    [Fact(DisplayName = "L4: GET /Settings/MealPlanning with no defaults set renders the form without error")]
    public async Task Get_NoDefaults_RendersForm()
    {
        var client = MakeClient(_factory);

        var response = await client.GetAsync("/Settings/MealPlanning");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("meal-planning-defaults-form", html);
        Assert.Contains("Meal planning defaults", html);
    }

    // ── 3. POST saves and returns updated partial with Saved badge ────────────

    [Fact(DisplayName = "L4: POST ?handler=SetMealPlanningDefaults persists household default and returns Saved badge")]
    public async Task Post_SetMealPlanningDefaults_ReturnsSavedBadge()
    {
        await using var factory = new MealPlanningSettingsMutableFactory();
        var client = MakeClient(factory);

        // GET first to obtain a valid antiforgery token
        var getResp = await client.GetAsync("/Settings/MealPlanning");
        getResp.EnsureSuccessStatusCode();
        var getHtml = await getResp.Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(getHtml);

        var form = new FormUrlEncodedContent([
            new("__RequestVerificationToken", token),
            new("budget", "200"),
            new("wasteWeight", "60"),
            new("costWeight", "20"),
            new("varietyWeight", "20"),
        ]);

        var response = await client.PostAsync("/Settings/MealPlanning?handler=SetMealPlanningDefaults", form);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // The Saved badge must appear in the response fragment
        Assert.Contains("Defaults saved", html);
        // The updated budget value must be reflected in the returned form
        Assert.Contains("200", html);
    }

    // ── 4. Unauthenticated returns 401 ────────────────────────────────────────

    [Fact(DisplayName = "L4: Unauthenticated GET /Settings/MealPlanning returns 401")]
    public async Task Unauthenticated_Returns_401()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        // No household header → TestAuthHandler returns no result → 401

        var response = await client.GetAsync("/Settings/MealPlanning");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static HttpClient MakeClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, MealPlanningSettingsFixture.HouseholdId.ToString());
        return client;
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the Settings/MealPlanning page.");
        return match.Groups[1].Value;
    }
}

// ── Fixture ────────────────────────────────────────────────────────────────────

public static class MealPlanningSettingsFixture
{
    public static readonly Guid HouseholdId = Guid.Parse("dddddddd-0001-0000-0000-000000000001");
}

// ── Base factory (null settings repo — no defaults set) ────────────────────────

/// <summary>
/// Base L4 factory for /Settings/MealPlanning. Replaces only the planning settings
/// repositories with null stubs (no household default set). No Postgres required.
/// </summary>
public class MealPlanningSettingsFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.AddFakeExpiringSoonHorizon();
            // Auth: header-driven test scheme
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Null stubs — no household default set
            services.RemoveAll<IHouseholdPlanningSettingsRepository>();
            services.AddSingleton<IHouseholdPlanningSettingsRepository>(new NullSettingsRepo());

            services.RemoveAll<IWeekPlanningOverrideRepository>();
            services.AddSingleton<IWeekPlanningOverrideRepository>(new NullOverrideRepo());

            // Re-register SetPlanningSettingsService so it picks up the null repos
            services.RemoveAll<SetPlanningSettingsService>();
            services.AddScoped<SetPlanningSettingsService>();
        });
    }
}

// ── Seeded factory (pre-populated household default budget) ────────────────────

/// <summary>
/// Factory variant that seeds a household default budget so the GET can assert the round-trip.
/// </summary>
public sealed class MealPlanningSettingsSeededFactory : MealPlanningSettingsFactory
{
    private readonly decimal _budget;

    public MealPlanningSettingsSeededFactory(decimal budgetDecimal) => _budget = budgetDecimal;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.AddFakeExpiringSoonHorizon();
            var householdId = HouseholdId.From(MealPlanningSettingsFixture.HouseholdId);
            var settings = HouseholdPlanningSettings.Create(householdId);
            settings.SetDefaults(Money.FromDecimal(_budget, "USD"), null);

            services.RemoveAll<IHouseholdPlanningSettingsRepository>();
            services.AddSingleton<IHouseholdPlanningSettingsRepository>(
                new SeededSettingsRepo(settings));
        });
    }
}

// ── Mutable factory (in-memory repos for POST tests) ─────────────────────────

/// <summary>
/// Factory variant that uses mutable in-memory repos so the POST handler can upsert
/// the household default and the subsequent GET reflects the saved state.
/// </summary>
public sealed class MealPlanningSettingsMutableFactory : MealPlanningSettingsFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.AddFakeExpiringSoonHorizon();
            services.RemoveAll<IHouseholdPlanningSettingsRepository>();
            services.AddSingleton<IHouseholdPlanningSettingsRepository>(new MutableSettingsRepo());

            services.RemoveAll<IWeekPlanningOverrideRepository>();
            services.AddSingleton<IWeekPlanningOverrideRepository>(new NullOverrideRepo());

            services.RemoveAll<SetPlanningSettingsService>();
            services.AddScoped<SetPlanningSettingsService>();
        });
    }
}

// ── Repository stubs ─────────────────────────────────────────────────────────

internal sealed class NullSettingsRepo : IHouseholdPlanningSettingsRepository
{
    public Task<HouseholdPlanningSettings?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        => Task.FromResult<HouseholdPlanningSettings?>(null);
    public Task AddAsync(HouseholdPlanningSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class NullOverrideRepo : IWeekPlanningOverrideRepository
{
    public Task<WeekPlanningOverride?> FindAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
        => Task.FromResult<WeekPlanningOverride?>(null);
    public Task AddAsync(WeekPlanningOverride weekOverride, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class SeededSettingsRepo(HouseholdPlanningSettings settings) : IHouseholdPlanningSettingsRepository
{
    public Task<HouseholdPlanningSettings?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        => Task.FromResult<HouseholdPlanningSettings?>(settings);
    public Task AddAsync(HouseholdPlanningSettings s, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class MutableSettingsRepo : IHouseholdPlanningSettingsRepository
{
    private HouseholdPlanningSettings? _stored;

    public Task<HouseholdPlanningSettings?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        => Task.FromResult(_stored);

    public Task AddAsync(HouseholdPlanningSettings settings, CancellationToken ct = default)
    {
        _stored = settings;
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}
