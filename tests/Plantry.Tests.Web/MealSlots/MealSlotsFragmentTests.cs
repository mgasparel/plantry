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

namespace Plantry.Tests.Web.MealSlots;

/// <summary>
/// L4 fragment tests for the /Settings/MealSlots page (P3-1).
/// Uses the WAF harness with fake services — no Postgres touched.
/// </summary>
public sealed class MealSlotsFragmentTests : IClassFixture<MealSlotsFragmentFactory>
{
    private readonly MealSlotsFragmentFactory _factory;

    public MealSlotsFragmentTests(MealSlotsFragmentFactory factory) => _factory = factory;

    [Fact(DisplayName = "GET /Settings/MealSlots renders the slots card with default 3 slots")]
    public async Task Get_Page_Renders_Three_Default_Slots()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            MealSlotsFixture.HouseholdAId.ToString());

        var response = await client.GetAsync("/Settings/MealSlots");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Breakfast", html);
        Assert.Contains("Lunch", html);
        Assert.Contains("Dinner", html);
        Assert.Contains("slot-card", html);
    }

    [Fact(DisplayName = "GET /Settings/MealSlots?handler=Slots returns _SlotsList partial only")]
    public async Task GetSlots_Handler_Returns_Partial()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            MealSlotsFixture.HouseholdAId.ToString());

        var response = await client.GetAsync("/Settings/MealSlots?handler=Slots");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Should have the slots region content
        Assert.Contains("Daily meals", html);
        Assert.Contains("Breakfast", html);
    }

    [Fact(DisplayName = "Unauthenticated GET /Settings/MealSlots returns 401 (test auth scheme)")]
    public async Task Unauthenticated_Returns_401()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        // No household header → TestAuthHandler returns no result → 401

        var response = await client.GetAsync("/Settings/MealSlots");

        // The test auth scheme returns 401 (not 302) since it is not a cookie scheme.
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact(DisplayName = "POST Add with antiforgery token returns updated _SlotsList partial with new slot")]
    public async Task Post_Add_Returns_Updated_Partial()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            MealSlotsFixture.HouseholdAId.ToString());

        // GET the page first to obtain the antiforgery token + paired cookie.
        var pageHtml = await (await client.GetAsync("/Settings/MealSlots")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var content = new FormUrlEncodedContent([
            new("__RequestVerificationToken", token),
            new("label", "Supper"),
        ]);

        var response = await client.PostAsync("/Settings/MealSlots?handler=Add", content);

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Supper", html);
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

/// <summary>
/// Fixture data for the MealSlots L4 tests.
/// </summary>
public static class MealSlotsFixture
{
    public static readonly Guid HouseholdAId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly HouseholdId Household = HouseholdId.From(HouseholdAId);
    private static readonly IClock Clock = Plantry.SharedKernel.Domain.SystemClock.Instance;

    public static MealSlotConfig BuildConfig()
        => MealSlotConfig.CreateWithDefaults(Household, Clock);

    public static IReadOnlyList<HouseholdMember> Members =>
        [new HouseholdMember(Guid.Parse("dddddddd-0000-0000-0000-000000000001"), "Alice")];
}

/// <summary>
/// L4 WebApplicationFactory for the MealSlots page. Replaces all Postgres-backed
/// and Identity seams with in-memory fakes so no database is needed.
/// </summary>
public sealed class MealSlotsFragmentFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // Auth: header-driven test scheme (same as other L4 tests).
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Replace the MealSlot config repository with a fake holding the fixture config.
            var config = MealSlotsFixture.BuildConfig();
            services.RemoveAll<IMealSlotConfigRepository>();
            services.AddScoped<IMealSlotConfigRepository>(_ => new FakeMealSlotConfigRepo(config));

            // Replace the household member reader with a fake.
            services.RemoveAll<IHouseholdMemberReader>();
            services.AddSingleton<IHouseholdMemberReader>(
                new FakeHouseholdMemberReaderStub(MealSlotsFixture.Members));

            // Re-register ManageSlotsService so it picks up the new fakes.
            services.RemoveAll<ManageSlotsService>();
            services.AddScoped<ManageSlotsService>();
        });
    }
}

// ── fakes ─────────────────────────────────────────────────────────────────────

public sealed class FakeMealSlotConfigRepo(MealSlotConfig config) : IMealSlotConfigRepository
{
    private MealSlotConfig _config = config;

    public Task<MealSlotConfig?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        => Task.FromResult<MealSlotConfig?>(_config);

    public Task AddAsync(MealSlotConfig c, CancellationToken ct = default)
    {
        _config = c;
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class FakeHouseholdMemberReaderStub(IReadOnlyList<HouseholdMember> members) : IHouseholdMemberReader
{
    public Task<IReadOnlyList<HouseholdMember>> GetMembersAsync(
        Guid householdId, CancellationToken ct = default)
        => Task.FromResult(members);
}
