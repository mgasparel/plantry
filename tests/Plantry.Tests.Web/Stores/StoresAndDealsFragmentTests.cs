using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Stores;

/// <summary>
/// L4 fragment tests for the §7e /Settings/StoresAndDeals page (P5-2). Uses the WAF harness with
/// in-memory fakes for the Deals repository + Catalog store ports — no Postgres touched. The real
/// <c>StubFlyerSourceAdapter</c> supplies the canned directory so the search fragment is exercised
/// end-to-end.
/// </summary>
public sealed class StoresAndDealsFragmentTests(StoresAndDealsFragmentFactory factory)
    : IClassFixture<StoresAndDealsFragmentFactory>
{
    private static readonly Guid HouseholdId = Guid.Parse("cccccccc-0000-0000-0000-0000000000e5");

    private HttpClient AuthedClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdId.ToString());
        return client;
    }

    [Fact(DisplayName = "GET /Settings/StoresAndDeals renders the empty-state store list")]
    public async Task Get_Page_Renders_Empty_State()
    {
        factory.Repo.Items.Clear();
        var client = AuthedClient();

        var response = await client.GetAsync("/Settings/StoresAndDeals");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Stores &amp; Deals", html);
        Assert.Contains("No stores yet", html);
        Assert.Contains("Add a store", html);
    }

    [Fact(DisplayName = "GET ?handler=Search with a postal code returns the canned directory results")]
    public async Task Search_Returns_Directory_Results()
    {
        var client = AuthedClient();

        var response = await client.GetAsync("/Settings/StoresAndDeals?handler=Search&postalCode=K1A0B1");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("FreshCo", html);
        Assert.Contains("Subscribe", html);
    }

    [Fact(DisplayName = "GET ?handler=Search filters by store name")]
    public async Task Search_Filters_By_Name()
    {
        var client = AuthedClient();

        var response = await client.GetAsync("/Settings/StoresAndDeals?handler=Search&postalCode=K1A0B1&q=metro");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Metro", html);
        Assert.DoesNotContain("FreshCo", html);
    }

    [Fact(DisplayName = "POST Subscribe returns the list fragment with the store active in 'not pulled yet' state")]
    public async Task Post_Subscribe_Shows_Active_Not_Pulled_Yet()
    {
        factory.Repo.Items.Clear();
        var client = AuthedClient();
        var token = await GetTokenAsync(client);

        var response = await client.PostAsync("/Settings/StoresAndDeals?handler=Subscribe",
            new FormUrlEncodedContent([
                new("__RequestVerificationToken", token),
                new("externalRef", "flipp-freshco"),
                new("name", "FreshCo"),
                new("postalCode", "K1A0B1"),
            ]));

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("FreshCo", html);
        Assert.Contains("Not pulled yet", html);
        Assert.Contains("Unsubscribe", html);
        Assert.Single(factory.Repo.Items);
    }

    [Fact(DisplayName = "POST Pause moves the subscription into the Paused section")]
    public async Task Post_Pause_Shows_Paused()
    {
        factory.Repo.Items.Clear();
        var client = AuthedClient();
        var token = await GetTokenAsync(client);

        await client.PostAsync("/Settings/StoresAndDeals?handler=Subscribe",
            new FormUrlEncodedContent([
                new("__RequestVerificationToken", token),
                new("externalRef", "flipp-metro"),
                new("name", "Metro"),
                new("postalCode", "K1A0B1"),
            ]));

        var subId = factory.Repo.Items.Single().Id.Value;

        var response = await client.PostAsync(
            $"/Settings/StoresAndDeals?handler=Pause&id={subId}",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Paused", html);
        Assert.Contains("Resume", html);
        Assert.False(factory.Repo.Items.Single().IsActive);
    }

    [Fact(DisplayName = "Unauthenticated GET returns 401 (test auth scheme)")]
    public async Task Unauthenticated_Returns_401()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Settings/StoresAndDeals");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<string> GetTokenAsync(HttpClient client)
    {
        var html = await (await client.GetAsync("/Settings/StoresAndDeals")).Content.ReadAsStringAsync();
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the page.");
        return match.Groups[1].Value;
    }
}

/// <summary>
/// L4 WebApplicationFactory for the Stores &amp; Deals page. Replaces the Postgres-backed Deals
/// repository and the Catalog store ports with in-memory fakes so no database is needed; keeps the real
/// stub flyer source and ManageSubscriptions.
/// </summary>
public sealed class StoresAndDealsFragmentFactory : WebApplicationFactory<Program>
{
    public FakeStoreSubscriptionRepo Repo { get; } = new();
    public FakeCatalogStorePort StorePort { get; } = new();

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

            services.RemoveAll<IStoreSubscriptionRepository>();
            services.AddScoped<IStoreSubscriptionRepository>(_ => Repo);

            services.RemoveAll<ICatalogStoreReader>();
            services.AddScoped<ICatalogStoreReader>(_ => StorePort);
            services.RemoveAll<ICatalogStoreWriter>();
            services.AddScoped<ICatalogStoreWriter>(_ => StorePort);

            services.RemoveAll<ManageSubscriptions>();
            services.AddScoped<ManageSubscriptions>();
        });
    }
}

// ── fakes ───────────────────────────────────────────────────────────────────────

public sealed class FakeStoreSubscriptionRepo : IStoreSubscriptionRepository
{
    public List<StoreSubscription> Items { get; } = [];

    public Task<StoreSubscription?> FindAsync(StoreSubscriptionId id, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(s => s.Id == id));

    public Task<StoreSubscription?> FindByStoreAsync(Guid storeId, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(s => s.StoreId == storeId));

    public Task<List<StoreSubscription>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(Items.OrderBy(s => s.CreatedAt).ToList());

    public Task AddAsync(StoreSubscription subscription, CancellationToken ct = default)
    {
        Items.Add(subscription);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Combined fake for both Catalog store ports: ensure mints one stable id+name per external_ref
/// and the reader resolves those names back.</summary>
public sealed class FakeCatalogStorePort : ICatalogStoreReader, ICatalogStoreWriter
{
    private readonly Dictionary<string, (Guid Id, string Name)> _byRef = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> _names = new();

    public Task<Guid> EnsureAsync(string externalRef, string name, CancellationToken ct = default)
    {
        if (!_byRef.TryGetValue(externalRef, out var entry))
        {
            entry = (Guid.NewGuid(), name);
            _byRef[externalRef] = entry;
            _names[entry.Id] = name;
        }
        return Task.FromResult(entry.Id);
    }

    public Task<CatalogStoreInfo?> FindAsync(Guid storeId, CancellationToken ct = default) =>
        Task.FromResult(_names.TryGetValue(storeId, out var n)
            ? new CatalogStoreInfo(storeId, n, null)
            : null);

    public Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(
        IReadOnlyList<Guid> storeIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, string> result = storeIds
            .Where(_names.ContainsKey)
            .ToDictionary(id => id, id => _names[id]);
        return Task.FromResult(result);
    }
}
