using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Deals;

/// <summary>
/// L4 render tests for the /Deals page (P5-7 / DJ3). Uses the WAF harness with in-memory fakes for the
/// Deals repository + Catalog product/store ports — no Postgres touched. Proves the page renders the active
/// and pending sections, the auto-matched marker, the Review deep-link, the store/category grouping toggle,
/// and the subscribe-inviting empty state, over the real Razor page + <c>BrowseDeals</c> read service.
/// </summary>
public sealed class DealsPageTests(DealsPageFactory factory) : IClassFixture<DealsPageFactory>
{
    private static readonly Guid HouseholdId = Guid.Parse("dddddddd-0000-0000-0000-0000000000e5");

    private HttpClient AuthedClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdId.ToString());
        return client;
    }

    [Fact(DisplayName = "GET /Deals with no deals renders the subscribe-inviting empty state")]
    public async Task Empty_State_Invites_Subscription()
    {
        factory.Repo.Items.Clear();
        var client = AuthedClient();

        var response = await client.GetAsync("/Deals");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("No deals yet", html);
        Assert.Contains("/Settings/StoresAndDeals", html);
    }

    [Fact(DisplayName = "GET /Deals renders active deals, the auto-matched marker, and the grouping toggle")]
    public async Task Renders_Active_With_Marker_And_Toggle()
    {
        factory.Seed();
        var client = AuthedClient();

        var response = await client.GetAsync("/Deals");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Active deals", html);
        Assert.Contains("Whole Milk", html);   // resolved product name (active/confirmed)
        Assert.Contains("FreshCo", html);        // resolved store name
        Assert.Contains("Auto-matched", html);   // DL-O3 marker on the auto-confirmed deal
        // Store/category grouping toggle.
        Assert.Contains("By store", html);
        Assert.Contains("By category", html);
        Assert.Contains("Dairy", html);          // category group header
    }

    [Fact(DisplayName = "GET /Deals renders the pending section with a Review entry deep-linking to the queue")]
    public async Task Renders_Pending_With_Review_Link()
    {
        factory.Seed();
        var client = AuthedClient();

        var response = await client.GetAsync("/Deals");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("to review", html);                 // pending count badge
        Assert.Contains("Review deals", html);              // Review entry
        Assert.Contains(Plantry.Web.Pages.Deals.IndexModel.ReviewQueueUrl, html); // deep-link into P5-8
    }

    [Fact(DisplayName = "Unauthenticated GET /Deals returns 401")]
    public async Task Unauthenticated_Returns_401()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Deals");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── P5-10 stock-up alerts (DJ5) ──────────────────────────────────────────────

    [Fact(DisplayName = "GET /Deals renders a stock-up alert for a frequently-bought product on an active deal")]
    public async Task Renders_StockUp_Alert_With_AddToList()
    {
        factory.Seed(); // makes Whole Milk "frequent" and on an active deal
        var client = AuthedClient();

        var response = await client.GetAsync("/Deals");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Stock up", html);        // alert surface heading
        Assert.Contains("Whole Milk", html);      // the frequent-∩-active-deal product
        Assert.Contains("Add to list", html);     // the reused P2-4 seam action
        Assert.Contains("FreshCo", html);         // cheapest active deal's store
        // The alert POSTs the product + deal ids over the "Add to list" handler.
        Assert.Contains("handler=AddToList", html);
        Assert.Contains(factory.AlertProductId.ToString(), html);
        Assert.Contains(factory.AlertDealId.ToString(), html);
    }

    [Fact(DisplayName = "POST /Deals?handler=AddToList places the product on the shopping list via the deal seam")]
    public async Task AddToList_Places_Item_Via_Seam()
    {
        factory.Seed();
        var client = AuthedClient();

        // Fetch the page to obtain the antiforgery token + cookie.
        var html = await (await client.GetAsync("/Deals")).Content.ReadAsStringAsync();
        var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(m.Success, "No antiforgery token found on the Deals page.");
        var token = m.Groups[1].Value;

        var response = await client.PostAsync("/Deals?handler=AddToList", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("productId", factory.AlertProductId.ToString()),
            new KeyValuePair<string, string>("dealId", factory.AlertDealId.ToString()),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        }));

        response.EnsureSuccessStatusCode();
        Assert.Contains(
            factory.Writer.Added,
            a => a.ProductId == factory.AlertProductId && a.DealId.Value == factory.AlertDealId);
    }
}

/// <summary>
/// L4 WebApplicationFactory for the Deals page. Replaces the Postgres-backed Deal repository and the Catalog
/// product/store ports with in-memory fakes so no database is needed; the real <c>BrowseDeals</c> service
/// runs over them. <see cref="Seed"/> stages one auto-confirmed + one user-confirmed active deal (both
/// in-window) and one pending deal.
/// </summary>
public sealed class DealsPageFactory : WebApplicationFactory<Program>
{
    private static readonly Guid Store = Guid.NewGuid();
    private static readonly Guid MilkProduct = Guid.NewGuid();
    private static readonly Guid BreadProduct = Guid.NewGuid();

    public FakeDealBrowseRepo Repo { get; } = new();
    public FakeDealProductReader Products { get; } = new();
    public FakeDealStoreReader Stores { get; } = new();
    public FakeDealFrequency Frequency { get; } = new();
    public FakeDealShoppingWriter Writer { get; } = new();

    /// <summary>The product + deal id of the seeded stock-up alert (Whole Milk, made "frequent" in <see cref="Seed"/>).</summary>
    public Guid AlertProductId { get; private set; }
    public Guid AlertDealId { get; private set; }

    public void Seed()
    {
        Repo.Items.Clear();
        Frequency.Counts.Clear();
        Stores.Names[Store] = "FreshCo";
        Products.Items[MilkProduct] = new DealProductInfo(MilkProduct, "Whole Milk", "Dairy");
        Products.Items[BreadProduct] = new DealProductInfo(BreadProduct, "Sourdough", "Bakery");

        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var window = ValidityWindow.Create(today.AddDays(-1), today.AddDays(6)).Value;

        var auto = Stage("Milk 2L", window, MilkProduct, clock);
        auto.AutoConfirm(MilkProduct, clock);
        Repo.Items.Add(auto);

        var confirmed = Stage("Sourdough Loaf", window, BreadProduct, clock);
        confirmed.Confirm(BreadProduct, Guid.NewGuid(), clock);
        Repo.Items.Add(confirmed);

        var pending = Stage("Fresh Salmon", window, suggested: null, clock: clock);
        Repo.Items.Add(pending);

        // Make Whole Milk "frequently bought" so it surfaces as a stock-up alert (frequent ∩ active-deal).
        Frequency.Counts[MilkProduct] = 5;
        AlertProductId = MilkProduct;
        AlertDealId = auto.Id.Value;
    }

    private static Deal Stage(string rawName, ValidityWindow window, Guid? suggested, IClock clock)
    {
        var raw = new RawDeal(rawName, null, null, 4.99m, null, null, "Save $1", window);
        var proposal = suggested is { } s ? new MatchProposal(s, MatchConfidence.Low, "maybe") : MatchProposal.Unmatched();
        return Deal.Stage(HouseholdId.New(), FlyerImportId.New(), Store, raw, DealNormalizer.Normalize(rawName), proposal, clock);
    }

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

            services.RemoveAll<IDealRepository>();
            services.AddScoped<IDealRepository>(_ => Repo);
            services.RemoveAll<ICatalogProductReader>();
            services.AddScoped<ICatalogProductReader>(_ => Products);
            services.RemoveAll<ICatalogStoreReader>();
            services.AddScoped<ICatalogStoreReader>(_ => Stores);
            // Stock-up alerts (P5-10): fake the Inventory frequency read + the Shopping writer so no
            // Postgres/Inventory stack is needed; the real StockUpAlerts service runs over BrowseDeals.
            services.RemoveAll<IPurchaseFrequencyReader>();
            services.AddScoped<IPurchaseFrequencyReader>(_ => Frequency);
            services.RemoveAll<IDealShoppingListWriter>();
            services.AddScoped<IDealShoppingListWriter>(_ => Writer);
        });
    }
}

// ── fakes ───────────────────────────────────────────────────────────────────────

public sealed class FakeDealBrowseRepo : IDealRepository
{
    public List<Deal> Items { get; } = [];

    public Task<Deal?> FindAsync(DealId id, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(d => d.Id == id));

    public Task<List<Deal>> ListBrowsableAsync(CancellationToken ct = default) =>
        Task.FromResult(Items.Where(d => d.Status is DealStatus.Pending or DealStatus.Confirmed).ToList());

    public Task<List<Deal>> ListByFlyerImportAsync(FlyerImportId flyerImportId, CancellationToken ct = default) =>
        Task.FromResult(Items.Where(d => d.FlyerImportId == flyerImportId).ToList());

    public Task AddAsync(Deal deal, CancellationToken ct = default) { Items.Add(deal); return Task.CompletedTask; }
    public void Remove(Deal deal) => Items.Remove(deal);
    public void DiscardStagedChanges() { } // no deferred change tracker to reset in this browse fake
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class FakeDealProductReader : ICatalogProductReader
{
    public Dictionary<Guid, DealProductInfo> Items { get; } = new();

    public Task<bool> ExistsAsync(Guid productId, CancellationToken ct = default) => Task.FromResult(true);
    public Task<IReadOnlyList<ProductCandidate>> ListCandidatesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProductCandidate>>([]);

    public Task<IReadOnlyDictionary<Guid, DealProductInfo>> ForProductsAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, DealProductInfo> result = productIds
            .Where(Items.ContainsKey)
            .ToDictionary(id => id, id => Items[id]);
        return Task.FromResult(result);
    }
}

public sealed class FakeDealStoreReader : ICatalogStoreReader
{
    public Dictionary<Guid, string> Names { get; } = new();

    public Task<CatalogStoreInfo?> FindAsync(Guid storeId, CancellationToken ct = default) =>
        Task.FromResult(Names.TryGetValue(storeId, out var n) ? new CatalogStoreInfo(storeId, n, null) : null);

    public Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(
        IReadOnlyList<Guid> storeIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, string> result = storeIds
            .Where(Names.ContainsKey)
            .ToDictionary(id => id, id => Names[id]);
        return Task.FromResult(result);
    }
}

/// <summary>In-memory <see cref="IPurchaseFrequencyReader"/> — product id → purchase count in the window.</summary>
public sealed class FakeDealFrequency : IPurchaseFrequencyReader
{
    public Dictionary<Guid, int> Counts { get; } = new();

    public Task<IReadOnlyDictionary<Guid, int>> PurchaseCountsSinceAsync(
        DateTimeOffset since, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, int>>(new Dictionary<Guid, int>(Counts));
}

/// <summary>Records every "Add to list" call so the page POST test can assert the item was placed.</summary>
public sealed class FakeDealShoppingWriter : IDealShoppingListWriter
{
    public List<(Guid ProductId, DealId DealId)> Added { get; } = [];

    public Task AddItemAsync(Guid productId, DealId dealId, CancellationToken ct = default)
    {
        Added.Add((productId, dealId));
        return Task.CompletedTask;
    }
}
