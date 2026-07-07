using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Catalog.Domain;
using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;
using CatalogUnit = Plantry.Catalog.Domain.Unit;
using SystemClock = Plantry.SharedKernel.Domain.SystemClock;

namespace Plantry.Tests.Web.Deals;

/// <summary>
/// L4/L5 tests for the deal review queue (P5-8 / DJ4). Uses the WAF harness with in-memory fakes for the
/// Deals repository, Catalog product/store ports, the match-memory repo, the price-observation writer, and
/// the Catalog reference/create repos — no Postgres touched. The real <c>ReviewDeals</c>,
/// <c>ConfirmDeal</c>, and <c>RejectDeal</c> run over the fakes, so these tests prove the verb wiring
/// (Confirm/Correct/Reject → the P5-5 commands, including inline-create) end to end, plus the
/// confidence-shaped render and the single-suggestion chip.
/// </summary>
public sealed class DealReviewPageTests(DealReviewFactory factory) : IClassFixture<DealReviewFactory>
{
    private static readonly Guid HouseholdId = Guid.Parse("dddddddd-0000-0000-0000-0000000000f8");

    private HttpClient AuthedClient()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdId.ToString());
        return client;
    }

    private static async Task<string> TokenAsync(HttpClient client)
    {
        var html = await (await client.GetAsync("/Deals/Review")).Content.ReadAsStringAsync();
        var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(m.Success, "No antiforgery token found on the review page.");
        return m.Groups[1].Value;
    }

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client, string url, params KeyValuePair<string, string>[] fields) =>
        client.PostAsync(url, new FormUrlEncodedContent(fields));

    private static KeyValuePair<string, string> Kv(string key, string value) => new(key, value);

    // ── L4 render ────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "GET /Deals/Review with no pending deals renders the caught-up empty state")]
    public async Task Empty_Queue_Renders_Caught_Up()
    {
        factory.Reset();
        var html = await (await AuthedClient().GetAsync("/Deals/Review")).Content.ReadAsStringAsync();
        Assert.Contains("All caught up", html);
    }

    [Fact(DisplayName = "GET /Deals/Review renders each confidence treatment + the single-suggestion chip")]
    public async Task Renders_Confidence_Treatments()
    {
        factory.Reset();
        factory.SeedPending("Milk 2L", MatchConfidence.High, factory.MilkProduct);
        factory.SeedPending("Sourdough Loaf", MatchConfidence.Low, factory.BreadProduct);
        factory.SeedPending("Mystery Item", MatchConfidence.None, suggested: null);

        var html = await (await AuthedClient().GetAsync("/Deals/Review")).Content.ReadAsStringAsync();

        // Raw flyer names render verbatim (ACL quarantine).
        Assert.Contains("Milk 2L", html);
        Assert.Contains("Mystery Item", html);
        // _ConfidenceBadge treatment (High/Low/None) reused from Intake.
        Assert.Contains("Matched", html);      // High
        Assert.Contains("Check match", html);  // Low
        Assert.Contains("No match", html);     // None
        // Single-suggestion "did you mean" chip for a matched deal.
        Assert.Contains("Did you mean", html);
        Assert.Contains("Whole Milk", html);   // resolved suggestion name
        // The per-card "Unrecognized — no catalog match…" boilerplate is gone (q9zr.2 item 2): the badge +
        // verbs already communicate it, so it no longer repeats once per no-match row.
        Assert.DoesNotContain("Unrecognized", html);
        Assert.DoesNotContain("no catalog match", html);
        // Verbs present.
        Assert.Contains("Confirm", html);
        Assert.Contains("Correct", html);
        Assert.Contains("Reject", html);
    }

    [Fact(DisplayName = "GET /Deals/Review groups the queue into High → Low → None tier sections with counts")]
    public async Task Groups_Into_Confidence_Tier_Sections()
    {
        factory.Reset();
        factory.SeedPending("High A", MatchConfidence.High, factory.MilkProduct);
        factory.SeedPending("High B", MatchConfidence.High, factory.BreadProduct);
        factory.SeedPending("Low A", MatchConfidence.Low, factory.BreadProduct);
        factory.SeedPending("None A", MatchConfidence.None, suggested: null);
        factory.SeedPending("None B", MatchConfidence.None, suggested: null);
        factory.SeedPending("None C", MatchConfidence.None, suggested: null);

        var html = System.Net.WebUtility.HtmlDecode(
            await (await AuthedClient().GetAsync("/Deals/Review")).Content.ReadAsStringAsync());

        // The three tier section headers render in fixed High → Low → None order.
        var high = html.IndexOf("Looks right", StringComparison.Ordinal);
        var low = html.IndexOf("Needs a look", StringComparison.Ordinal);
        var none = html.IndexOf("Not in your catalog", StringComparison.Ordinal);
        Assert.True(high >= 0 && low >= 0 && none >= 0, "All three tier sections should render.");
        Assert.True(high < low && low < none, "Tier sections must be ordered High → Low → None.");

        // Each section header carries its own count (High 2 / Low 1 / None 3) immediately after the title.
        Assert.Matches(@"Looks right</span>\s*<span class=""ch-sub"">·\s*2", html);
        Assert.Matches(@"Needs a look</span>\s*<span class=""ch-sub"">·\s*1", html);
        Assert.Matches(@"Not in your catalog</span>\s*<span class=""ch-sub"">·\s*3", html);
    }

    [Fact(DisplayName = "GET /Deals/Review?dealId=<confirmed> renders the single auto-matched correction card")]
    public async Task Renders_Single_Correction_For_Confirmed_Deal()
    {
        factory.Reset();
        var deal = factory.SeedAutoConfirmed("Milk 2L", factory.MilkProduct);

        var html = await (await AuthedClient().GetAsync($"/Deals/Review?dealId={deal.Id.Value}"))
            .Content.ReadAsStringAsync();

        Assert.Contains("Currently matched to", html);
        Assert.Contains("Whole Milk", html);
        Assert.Contains("Correct", html);
        Assert.Contains("Reject", html);
    }

    [Fact(DisplayName = "Unauthenticated GET /Deals/Review returns 401")]
    public async Task Unauthenticated_Returns_401()
    {
        factory.Reset();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/Deals/Review");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact(DisplayName = "GET /Deals/Review flags a $0.00 flyer-noise row and de-emphasises it")]
    public async Task Flags_Flyer_Noise_Rows()
    {
        factory.Reset();
        factory.SeedPending("AD MATCH", MatchConfidence.None, suggested: null, price: 0m);
        factory.SeedPending("Real Deal", MatchConfidence.None, suggested: null, price: 3.49m);

        var html = System.Net.WebUtility.HtmlDecode(
            await (await AuthedClient().GetAsync("/Deals/Review")).Content.ReadAsStringAsync());

        // The $0.00 row is flagged and gets the de-emphasis modifier; the priced row does not.
        Assert.Contains("Flyer noise", html);
        Assert.Contains("deal-review-row--noise", html);
        Assert.Single(Regex.Matches(html, "deal-review-row--noise"));
    }

    [Fact(DisplayName = "GET /Deals/Review title-cases raw names for display, keeping the verbatim string in the title attribute")]
    public async Task Title_Cases_Raw_Names_For_Display()
    {
        factory.Reset();
        factory.SeedPending("FRANK'S HOT SAUCE 375ML", MatchConfidence.None, suggested: null);

        var html = System.Net.WebUtility.HtmlDecode(
            await (await AuthedClient().GetAsync("/Deals/Review")).Content.ReadAsStringAsync());

        // Display is title-cased — capitalised after the start and spaces, but NOT after the apostrophe.
        Assert.Contains("Frank's Hot Sauce 375ml", html);
        Assert.DoesNotContain("Frank'S", html);   // no capital letter after the apostrophe
        // The verbatim ALL-CAPS flyer string is preserved untouched in the name's title attribute.
        Assert.Contains("title=\"FRANK'S HOT SAUCE 375ML\"", html);
    }

    // ── L5 verb wiring ────────────────────────────────────────────────────────────

    [Fact(DisplayName = "Confirm accepts the suggestion → deal becomes Confirmed, writes an observation, leaves the queue")]
    public async Task Confirm_Accepts_Suggestion()
    {
        factory.Reset();
        var deal = factory.SeedPending("Milk 2L", MatchConfidence.High, factory.MilkProduct);
        var client = AuthedClient();
        var token = await TokenAsync(client);

        var response = await PostAsync(client, $"/Deals/Review?handler=Confirm&dealId={deal.Id.Value}",
            Kv("__RequestVerificationToken", token));

        response.EnsureSuccessStatusCode();
        Assert.Equal(DealStatus.Confirmed, deal.Status);
        Assert.Equal(factory.MilkProduct, deal.ProductId);
        Assert.Equal(1, factory.Observations.Calls);          // deal-sourced observation written (confirm)
        var fragment = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Milk 2L", fragment);           // left the pending queue
    }

    [Fact(DisplayName = "Reject → deal becomes Rejected, writes NO observation, leaves the queue")]
    public async Task Reject_Writes_No_Observation()
    {
        factory.Reset();
        var deal = factory.SeedPending("Fresh Salmon", MatchConfidence.None, suggested: null);
        var client = AuthedClient();
        var token = await TokenAsync(client);

        var response = await PostAsync(client, $"/Deals/Review?handler=Reject&dealId={deal.Id.Value}",
            Kv("__RequestVerificationToken", token));

        response.EnsureSuccessStatusCode();
        Assert.Equal(DealStatus.Rejected, deal.Status);
        Assert.Null(deal.ProductId);
        Assert.Equal(0, factory.Observations.Calls);          // reject reaches Pricing never (D5)
        var fragment = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Fresh Salmon", fragment);
    }

    [Fact(DisplayName = "Correct with a different product → deal Confirmed to that product + memory repointed")]
    public async Task Correct_Resolves_Different_Product_And_Repoints_Memory()
    {
        factory.Reset();
        var deal = factory.SeedPending("Sourdough Loaf", MatchConfidence.Low, factory.BreadProduct);
        // Pre-seed a positive memory for this deal's key pointing at the ORIGINAL (bread) product.
        var memory = factory.SeedMemory(deal, factory.BreadProduct);
        var client = AuthedClient();
        var token = await TokenAsync(client);

        // Correct to the milk product (a DIFFERENT catalog product) via the search-view pick.
        var response = await PostAsync(client, "/Deals/Review?handler=Correct",
            Kv("__RequestVerificationToken", token),
            Kv("dealId", deal.Id.Value.ToString()),
            Kv("productId", factory.MilkProduct.ToString()));

        response.EnsureSuccessStatusCode();
        Assert.Equal(DealStatus.Confirmed, deal.Status);
        Assert.Equal(factory.MilkProduct, deal.ProductId);    // resolved a different product
        Assert.Equal(factory.MilkProduct, memory.ProductId);  // memory rewritten (repointed)
        Assert.Equal(1, factory.Observations.Calls);          // supersede observation written
    }

    [Fact(DisplayName = "Correct via inline-create → mints a catalog product, then confirms the deal against it")]
    public async Task Correct_Inline_Create_Then_Confirm()
    {
        factory.Reset();
        var deal = factory.SeedPending("Mystery Item", MatchConfidence.None, suggested: null);
        var client = AuthedClient();
        var token = await TokenAsync(client);

        var response = await PostAsync(client, "/Deals/Review?handler=Correct",
            Kv("__RequestVerificationToken", token),
            Kv("dealId", deal.Id.Value.ToString()),
            Kv("newProductName", "Artisan Mystery"),
            Kv("newProductUnitId", factory.UnitId.ToString()));

        response.EnsureSuccessStatusCode();
        // A catalog product was created …
        var created = Assert.Single(factory.Products.Items);
        Assert.Equal("Artisan Mystery", created.Name);
        // … and the deal was confirmed against the newly-created product.
        Assert.Equal(DealStatus.Confirmed, deal.Status);
        Assert.Equal(created.Id.Value, deal.ProductId);
        Assert.Equal(1, factory.Observations.Calls);
    }

    [Fact(DisplayName = "Correct an already-confirmed auto-matched deal → supersede (stays Confirmed, new product)")]
    public async Task Correct_Auto_Matched_Deal_Supersedes()
    {
        factory.Reset();
        var deal = factory.SeedAutoConfirmed("Milk 2L", factory.MilkProduct);
        Assert.True(deal.AutoMatched);
        var client = AuthedClient();
        var token = await TokenAsync(client);

        var response = await PostAsync(client, "/Deals/Review?handler=Correct",
            Kv("__RequestVerificationToken", token),
            Kv("dealId", deal.Id.Value.ToString()),
            Kv("productId", factory.BreadProduct.ToString()));

        response.EnsureSuccessStatusCode();
        Assert.Equal(DealStatus.Confirmed, deal.Status);      // still confirmed
        Assert.Equal(factory.BreadProduct, deal.ProductId);   // superseded to the new product
        Assert.False(deal.AutoMatched);                        // a manual correction clears the auto flag
        Assert.Equal(1, factory.Observations.Calls);          // append-only supersede observation
    }

    [Fact(DisplayName = "Search endpoint returns ranked <li> option markup for the correction sheet")]
    public async Task Search_Endpoint_Ranks_Candidates()
    {
        factory.Reset();
        var html = await (await AuthedClient().GetAsync("/Deals/Review?handler=SearchProducts&q=milk"))
            .Content.ReadAsStringAsync();
        Assert.Contains("<li", html);
        Assert.Contains("Whole Milk", html);
    }
}

/// <summary>
/// L4/L5 WebApplicationFactory for the deal review queue. Replaces the Postgres-backed Deals + Catalog
/// registrations with in-memory fakes; the real <c>ReviewDeals</c>/<c>ConfirmDeal</c>/<c>RejectDeal</c>
/// run over them. Seed helpers stage deals directly into the fake repository.
/// </summary>
public sealed class DealReviewFactory : WebApplicationFactory<Program>
{
    private static readonly Guid Store = Guid.NewGuid();
    public Guid MilkProduct { get; } = Guid.NewGuid();
    public Guid BreadProduct { get; } = Guid.NewGuid();
    public Guid UnitId { get; private set; }

    public FakeDealBrowseRepo Repo { get; } = new();
    public FakeReviewProductReader ProductReader { get; } = new();
    public FakeDealStoreReader Stores { get; } = new();
    public FakeReviewMemoryRepo Memories { get; } = new();
    public FakeReviewObservationWriter Observations { get; } = new();
    public FakeReviewUnitRepo Units { get; } = new();
    public FakeReviewCategoryRepo Categories { get; } = new();
    public FakeReviewProductRepo Products { get; } = new();
    public FakeReviewLocationRepo Locations { get; } = new();

    private static readonly IClock Clock = SystemClock.Instance;

    public DealReviewFactory()
    {
        UnitId = Units.Seed("g", "gram");
        ProductReader.Names[MilkProduct] = new DealProductInfo(MilkProduct, "Whole Milk", "Dairy");
        ProductReader.Names[BreadProduct] = new DealProductInfo(BreadProduct, "Sourdough", "Bakery");
        ProductReader.Candidates.Add(new ProductCandidate(MilkProduct, "Whole Milk"));
        ProductReader.Candidates.Add(new ProductCandidate(BreadProduct, "Sourdough"));
        Stores.Names[Store] = "FreshCo";
    }

    public void Reset()
    {
        Repo.Items.Clear();
        Memories.Items.Clear();
        Observations.Calls = 0;
        Products.Items.Clear();
    }

    private static ValidityWindow InWindow()
    {
        var today = DateOnly.FromDateTime(Clock.UtcNow.UtcDateTime);
        return ValidityWindow.Create(today.AddDays(-1), today.AddDays(6)).Value;
    }

    public Deal SeedPending(string rawName, MatchConfidence confidence, Guid? suggested, decimal price = 4.99m)
    {
        var raw = new RawDeal(rawName, "SomeBrand", null, price, null, null, "Save $1", InWindow());
        var proposal = suggested is { } s
            ? new MatchProposal(s, confidence, "looks like a match")
            : MatchProposal.Unmatched();
        var deal = Deal.Stage(
            HouseholdId.New(), FlyerImportId.New(), Store, raw, DealNormalizer.Normalize(rawName), proposal, Clock);
        Repo.Items.Add(deal);
        return deal;
    }

    public Deal SeedAutoConfirmed(string rawName, Guid product)
    {
        var deal = SeedPending(rawName, MatchConfidence.High, product);
        deal.AutoConfirm(product, Clock);
        return deal;
    }

    public DealMatchMemory SeedMemory(Deal deal, Guid product)
    {
        var normalized = new NormalizedName(deal.NormalizedName, DealNormalizer.NormalizerVersion);
        var memory = DealMatchMemory.Remember(
            deal.HouseholdId, deal.StoreId, normalized, deal.RawName, product, Guid.NewGuid(), Clock);
        Memories.Items.Add(memory);
        return memory;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.AddFakeExpiringSoonHorizon();
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
            services.AddScoped<ICatalogProductReader>(_ => ProductReader);
            services.RemoveAll<ICatalogStoreReader>();
            services.AddScoped<ICatalogStoreReader>(_ => Stores);
            services.RemoveAll<IDealMatchMemoryRepository>();
            services.AddScoped<IDealMatchMemoryRepository>(_ => Memories);
            services.RemoveAll<IPriceObservationWriter>();
            services.AddScoped<IPriceObservationWriter>(_ => Observations);

            services.RemoveAll<IUnitRepository>();
            services.AddScoped<IUnitRepository>(_ => Units);
            services.RemoveAll<ICategoryRepository>();
            services.AddScoped<ICategoryRepository>(_ => Categories);
            services.RemoveAll<IProductRepository>();
            services.AddScoped<IProductRepository>(_ => Products);
            services.RemoveAll<ILocationRepository>();
            services.AddScoped<ILocationRepository>(_ => Locations);
        });
    }
}

// ── fakes specific to the review page (Deal repo + store reader are shared from DealsPageTests) ──────

public sealed class FakeReviewProductReader : ICatalogProductReader
{
    public Dictionary<Guid, DealProductInfo> Names { get; } = new();
    public List<ProductCandidate> Candidates { get; } = [];

    public Task<bool> ExistsAsync(Guid productId, CancellationToken ct = default) => Task.FromResult(true);

    public Task<IReadOnlyList<ProductCandidate>> ListCandidatesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProductCandidate>>(Candidates);

    public Task<IReadOnlyDictionary<Guid, DealProductInfo>> ForProductsAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, DealProductInfo> result = productIds
            .Where(Names.ContainsKey)
            .ToDictionary(id => id, id => Names[id]);
        return Task.FromResult(result);
    }
}

public sealed class FakeReviewMemoryRepo : IDealMatchMemoryRepository
{
    public List<DealMatchMemory> Items { get; } = [];

    public Task<DealMatchMemory?> FindByKeyAsync(Guid storeId, string normalizedName, CancellationToken ct = default) =>
        Task.FromResult(Items.FirstOrDefault(m => m.StoreId == storeId && m.NormalizedName == normalizedName));

    public Task AddAsync(DealMatchMemory memory, CancellationToken ct = default)
    {
        Items.Add(memory);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class FakeReviewObservationWriter : IPriceObservationWriter
{
    public int Calls { get; set; }

    public Task<Guid> RecordObservationAsync(
        Guid productId, decimal price, decimal? quantity, Guid? unitId, Guid storeId,
        DateOnly validFrom, DateOnly validTo, Guid dealId, Guid? reviewedByUserId,
        DateTimeOffset observedAt, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(Guid.NewGuid());
    }
}

public sealed class FakeReviewUnitRepo : IUnitRepository
{
    private readonly List<CatalogUnit> _items = [];

    public Guid Seed(string code, string name)
    {
        var unit = CatalogUnit.Create(
            HouseholdId.From(Guid.NewGuid()), code, name, Dimension.Mass, factorToBase: 1m, isBase: true);
        _items.Add(unit);
        return unit.Id.Value;
    }

    public Task<CatalogUnit?> FindAsync(UnitId id, CancellationToken ct = default) =>
        Task.FromResult(_items.SingleOrDefault(u => u.Id == id));

    public Task<CatalogUnit?> FindByCodeAsync(string code, CancellationToken ct = default) =>
        Task.FromResult(_items.SingleOrDefault(u => u.Code.Equals(code, StringComparison.OrdinalIgnoreCase)));

    public Task<List<CatalogUnit>> ListAsync(CancellationToken ct = default) => Task.FromResult(_items.ToList());
    public Task AddAsync(CatalogUnit unit, CancellationToken ct = default) { _items.Add(unit); return Task.CompletedTask; }
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class FakeReviewCategoryRepo : ICategoryRepository
{
    public Task<Category?> FindAsync(CategoryId id, CancellationToken ct = default) => Task.FromResult<Category?>(null);
    public Task<Category?> FindByNameAsync(string name, CancellationToken ct = default) => Task.FromResult<Category?>(null);
    public Task<List<Category>> ListAsync(CancellationToken ct = default) => Task.FromResult(new List<Category>());
    public Task<List<Category>> ListActiveAsync(CancellationToken ct = default) => Task.FromResult(new List<Category>());
    public Task AddAsync(Category category, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class FakeReviewProductRepo : IProductRepository
{
    public List<Product> Items { get; } = [];

    public Task<Product?> FindAsync(ProductId id, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(p => p.Id == id));

    public Task<Product?> FindByNameAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)));

    public Task<List<Product>> ListActiveAsync(CancellationToken ct = default) =>
        Task.FromResult(Items.Where(p => p.ArchivedAt is null).ToList());

    public Task<List<Product>> ListActiveWithSkusAsync(CancellationToken ct = default) =>
        Task.FromResult(Items.Where(p => p.ArchivedAt is null).ToList());

    public Task<List<Product>> ListWithConversionsAsync(IEnumerable<ProductId> ids, CancellationToken ct = default) =>
        Task.FromResult(Items.Where(p => ids.Contains(p.Id)).ToList());

    public Task<List<Product>> ListVariantsAsync(ProductId parentId, CancellationToken ct = default) =>
        Task.FromResult(Items.Where(p => p.ParentProductId == parentId).ToList());

    public Task AddAsync(Product product, CancellationToken ct = default) { Items.Add(product); return Task.CompletedTask; }
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class FakeReviewLocationRepo : ILocationRepository
{
    public Task<Location?> FindAsync(LocationId id, CancellationToken ct = default) => Task.FromResult<Location?>(null);
    public Task<Location?> FindByNameAsync(string name, CancellationToken ct = default) => Task.FromResult<Location?>(null);
    public Task<List<Location>> ListAsync(CancellationToken ct = default) => Task.FromResult(new List<Location>());
    public Task<List<Location>> ListActiveAsync(CancellationToken ct = default) => Task.FromResult(new List<Location>());
    public Task AddAsync(Location location, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}
