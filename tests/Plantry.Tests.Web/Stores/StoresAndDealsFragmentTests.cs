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

    [Fact(DisplayName = "POST Subscribe flips the clicked result to the 'Subscribed' badge and OOB-syncs the list")]
    public async Task Post_Subscribe_Flips_Result_And_Syncs_List_Oob()
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

        // Criterion 1 — the clicked result flips to the reused badge--success "Subscribed" at the point of action.
        Assert.Contains("data-merchant=\"FreshCo\"", html);
        Assert.Contains("badge badge--success", html);
        Assert.Contains("Subscribed", html);
        // Criterion 2 — the subscription list rides along as an out-of-band swap (no re-search needed).
        Assert.Contains("id=\"stores-list-card\"", html);
        Assert.Contains("hx-swap-oob=\"true\"", html);
        Assert.Contains("Not pulled yet", html);
        Assert.Single(factory.Repo.Items);
    }

    [Fact(DisplayName = "Search results retarget the clicked result and disable the button in-flight")]
    public async Task Search_Results_Retarget_And_Disable_In_Flight()
    {
        factory.Repo.Items.Clear();
        var client = AuthedClient();

        var response = await client.GetAsync("/Settings/StoresAndDeals?handler=Search&postalCode=K1A0B1");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        // Criterion 1 — the subscribe form's primary swap targets the clicked result, not #subs-region.
        Assert.Contains("hx-target=\"closest .store-result\"", html);
        Assert.DoesNotContain("hx-target=\"#subs-region\"", html);
        // Criterion 3 — the button disables for the request duration so repeated clicks can't duplicate-subscribe.
        Assert.Contains("hx-disabled-elt=\"find button\"", html);
    }

    [Fact(DisplayName = "POST Subscribe with a blank postal code renders an inline error and retains the Subscribe button")]
    public async Task Post_Subscribe_Blank_Postal_Renders_Inline_Error_And_Retains_Button()
    {
        factory.Repo.Items.Clear();
        var client = AuthedClient();
        var token = await GetTokenAsync(client);

        var response = await client.PostAsync("/Settings/StoresAndDeals?handler=Subscribe",
            new FormUrlEncodedContent([
                new("__RequestVerificationToken", token),
                new("externalRef", "flipp-freshco"),
                new("name", "FreshCo"),
                new("postalCode", ""),
            ]));

        // Criterion 4 — the error fragment returns 200 so htmx actually swaps it (non-2xx bodies are ignored).
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("store-result__error", html);
        Assert.Contains("postal code", html, StringComparison.OrdinalIgnoreCase);
        // The Subscribe button is retained for retry; the result did NOT flip to the Subscribed badge.
        Assert.Contains(">Subscribe<", html);
        Assert.DoesNotContain("badge badge--success", html);
        Assert.Empty(factory.Repo.Items);
    }

    [Fact(DisplayName = "Searching after subscribing shows the already-subscribed result as a 'Subscribed' badge")]
    public async Task Search_After_Subscribe_Shows_Subscribed_Badge()
    {
        factory.Repo.Items.Clear();
        var client = AuthedClient();
        var token = await GetTokenAsync(client);

        await client.PostAsync("/Settings/StoresAndDeals?handler=Subscribe",
            new FormUrlEncodedContent([
                new("__RequestVerificationToken", token),
                new("externalRef", "flipp-freshco"),
                new("name", "FreshCo"),
                new("postalCode", "K1A0B1"),
            ]));

        var response = await client.GetAsync("/Settings/StoresAndDeals?handler=Search&postalCode=K1A0B1");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        // Criterion 5 — the already-subscribed search-result branch is unchanged: badge, no Subscribe form.
        var freshcoBlock = html[html.IndexOf("data-merchant=\"FreshCo\"", StringComparison.Ordinal)..];
        freshcoBlock = freshcoBlock[..freshcoBlock.IndexOf("</li>", StringComparison.Ordinal)];
        Assert.Contains("badge badge--success", freshcoBlock);
        Assert.DoesNotContain("handler=Subscribe", freshcoBlock);
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

        // Keep the canned StubFlyerSourceAdapter (not the real Flipp FlyerSource) so the directory-search
        // fragment is exercised deterministically with no live Flipp call. Honours the
        // Deals:UseStubFlyerSource seam in Program.cs.
        builder.UseSetting("Deals:UseStubFlyerSource", "true");

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

    public Task<List<StoreSubscription>> ListActiveAsync(CancellationToken ct = default) =>
        Task.FromResult(Items.Where(s => s.IsActive).OrderBy(s => s.CreatedAt).ToList());

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
