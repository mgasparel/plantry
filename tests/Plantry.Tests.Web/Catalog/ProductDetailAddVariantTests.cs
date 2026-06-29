using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Web.Infrastructure;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Web.Catalog;

/// <summary>
/// L4 Web integration tests for the "Add a variant" feature on the Product Detail page
/// (plantry-8r7o). Tests cover the stock-block acceptance criterion and the happy-path
/// variant creation through <c>OnPostAddVariantAsync</c>.
///
/// The catalog and inventory seams are replaced by in-memory fakes; no database is touched.
/// </summary>
public sealed class ProductDetailAddVariantTests : IDisposable
{
    private readonly ProductDetailAddVariantFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static readonly Guid HouseholdId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    private HttpClient AuthClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdId.ToString());
        return client;
    }

    /// <summary>
    /// Harvests the antiforgery token from the Detail page GET so the POST round-trips work.
    /// </summary>
    private async Task<string> GetAntiforgeryTokenAsync(HttpClient client, Guid productId)
    {
        var html = await (await client.GetAsync($"/Catalog/Products/{productId}"))
            .Content.ReadAsStringAsync();

        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the Detail page.");
        return match.Groups[1].Value;
    }

    // ── AC: Stock-block — standalone product with active stock is blocked ─────

    [Fact(DisplayName = "AddVariant — standalone product with active stock returns model error")]
    public async Task AddVariant_StandaloneWithStock_ReturnsModelError()
    {
        var client = AuthClient();
        var productId = ProductDetailAddVariantFixture.ProductWithStockId;

        var token = await GetAntiforgeryTokenAsync(client, productId);

        var response = await client.PostAsync(
            $"/Catalog/Products/{productId}?handler=AddVariant",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("AddVariantInput.Name", "Variant Name"),
            }));

        // Should return the page (not redirect) because there's a model error.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("currently holds stock", html);

        // SaveChangesAsync must not have been called — no variant was created.
        Assert.Equal(0, _factory.ProductRepo.SaveChangesCalls);
    }

    // ── AC: Happy path — standalone product with no stock creates a variant ──

    [Fact(DisplayName = "AddVariant — standalone product with no stock creates variant and redirects to it")]
    public async Task AddVariant_StandaloneNoStock_CreatesVariantAndRedirects()
    {
        var client = AuthClient();
        var productId = ProductDetailAddVariantFixture.ProductNoStockId;

        var token = await GetAntiforgeryTokenAsync(client, productId);

        var response = await client.PostAsync(
            $"/Catalog/Products/{productId}?handler=AddVariant",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("AddVariantInput.Name", "Skim Milk"),
            }));

        // Should redirect to the newly created variant's detail page.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.ToString();
        Assert.NotNull(location);
        Assert.Contains("/Catalog/Products/", location);

        // A new variant product should have been persisted (2 seeded + 1 new variant = 3).
        Assert.Equal(3, _factory.ProductRepo.Items.Count);
        // The variant is the only product that has a ParentProductId set.
        var variant = _factory.ProductRepo.Items.Single(p => p.IsVariant);
        Assert.Equal("Skim Milk", variant.Name);
    }
}

// ── Fixture data ──────────────────────────────────────────────────────────────

internal static class ProductDetailAddVariantFixture
{
    internal static readonly Guid HouseholdId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    internal static readonly HouseholdId Household = Plantry.SharedKernel.HouseholdId.From(HouseholdId);
    internal static readonly IClock Clock = Plantry.SharedKernel.Domain.SystemClock.Instance;

    // A standalone product that has active inventory stock (should be blocked from adding a variant).
    internal static readonly Guid ProductWithStockId = Guid.Parse("aaaaaaaa-0000-0000-0000-aaa000000001");

    // A standalone product with no inventory (can have a variant added).
    internal static readonly Guid ProductNoStockId = Guid.Parse("aaaaaaaa-0000-0000-0000-aaa000000002");

    internal static readonly Guid UnitId = Guid.Parse("bbbbbbbb-0000-0000-0000-bbb000000001");

    internal static CatalogUnit BuildUnit() =>
        CatalogUnit.Create(Household, "ea", "Each", Dimension.Count, 1m, isBase: true);

    internal static (Product WithStock, Product NoStock) BuildProducts(CatalogUnit unit)
    {
        var withStock = Product.Create(Household, "Whole Milk", unit.Id, Clock);
        var noStock = Product.Create(Household, "Milk Base", unit.Id, Clock);
        return (withStock, noStock);
    }

    internal static ProductStock BuildActiveStock()
    {
        // Use the fixture product ID (the route id), not the domain-generated Product.Id,
        // so the page handler's stocks.FindAsync(householdId, Id.Value) matches.
        var stock = ProductStock.Start(Household, ProductWithStockId, Clock);
        var userId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
        stock.AddStock(1m, UnitId, Guid.NewGuid(), userId, Clock);
        return stock;
    }
}

// ── WAF factory ───────────────────────────────────────────────────────────────

/// <summary>
/// L4 <see cref="WebApplicationFactory{TEntryPoint}"/> for the Product Detail "Add a variant" tests.
/// Replaces all Catalog domain repositories and <see cref="IProductStockRepository"/> with
/// in-memory fakes. No EF / Postgres touched.
/// </summary>
internal sealed class ProductDetailAddVariantFactory : WebApplicationFactory<Program>
{
    internal FakeProductRepo ProductRepo { get; private set; } = new();

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

            // Build fixture data.
            var unit = ProductDetailAddVariantFixture.BuildUnit();
            var (withStock, noStock) = ProductDetailAddVariantFixture.BuildProducts(unit);
            var stock = ProductDetailAddVariantFixture.BuildActiveStock();

            // Ensure the repository returns the products at their fixture Ids.
            // We use a capturing repo that exposes saved products for assertion.
            var productRepo = new FakeProductRepo();
            productRepo.AddWithId(withStock, ProductDetailAddVariantFixture.ProductWithStockId);
            productRepo.AddWithId(noStock, ProductDetailAddVariantFixture.ProductNoStockId);
            ProductRepo = productRepo;

            services.RemoveAll<IProductRepository>();
            services.AddScoped<IProductRepository>(_ => ProductRepo);

            // Unit repository: one "each" unit.
            services.RemoveAll<IUnitRepository>();
            services.AddSingleton<IUnitRepository>(new FakeSingleUnitRepository(unit));

            // Category / location repositories: empty is fine — the variant inherits from parent
            // and cross-ref validation in the command won't be called for the stock-block test,
            // and passes for nullable category/location in the create test.
            services.RemoveAll<ICategoryRepository>();
            services.AddSingleton<ICategoryRepository>(new FakeEmptyCategoryRepository());

            services.RemoveAll<ILocationRepository>();
            services.AddSingleton<ILocationRepository>(new FakeEmptyLocationRepository());

            // Stock repository: the product-with-stock product has an active lot; the other has none.
            var stockRepo = new FakeDetailStockRepository();
            stockRepo.Items.Add(stock);
            services.RemoveAll<IProductStockRepository>();
            services.AddSingleton<IProductStockRepository>(stockRepo);

            // Re-register ProductQueryService so it picks up the fake IProductRepository.
            services.RemoveAll<ProductQueryService>();
            services.AddScoped<ProductQueryService>();

            // Pricing seam: no prices.
            services.RemoveAll<Plantry.Pricing.Application.PricingQueries>();
            // PricingQueries is registered but not injected by the Detail page — no action needed.
        });
    }
}

// ── Fake implementations ──────────────────────────────────────────────────────

/// <summary>
/// In-memory <see cref="IProductRepository"/> for Detail page L4 tests.
/// Supports adding products with a fixed Id so the WAF route parameter matches.
/// </summary>
internal sealed class FakeProductRepo : IProductRepository
{
    // Mutable list to capture both seeded products and variants created by the command.
    internal List<Product> Items { get; } = [];
    internal int SaveChangesCalls { get; private set; }

    /// <summary>
    /// Adds a product but forces its Id to <paramref name="id"/> via the repository map
    /// so the route parameter in tests matches the seeded product.
    /// </summary>
    private readonly Dictionary<ProductId, Product> _byId = [];
    private readonly Dictionary<string, Product> _byName = [];

    internal void AddWithId(Product product, Guid id)
    {
        // Override the product's Id by constructing a surrogate lookup from the fixture id to
        // the actual product. The Detail page route calls FindAsync with the fixture id;
        // we intercept it and return the product.
        _byId[ProductId.From(id)] = product;
        _byName[product.Name.ToLowerInvariant()] = product;
        Items.Add(product);
    }

    public Task<Product?> FindAsync(ProductId id, CancellationToken ct = default) =>
        Task.FromResult(_byId.GetValueOrDefault(id));

    public Task<Product?> FindByNameAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(_byName.GetValueOrDefault(name.Trim().ToLowerInvariant()));

    public Task<List<Product>> ListActiveAsync(CancellationToken ct = default) =>
        Task.FromResult(Items.Where(p => !p.IsArchived).ToList());

    public Task<List<Product>> ListActiveWithSkusAsync(CancellationToken ct = default) =>
        Task.FromResult(Items.Where(p => !p.IsArchived).ToList());

    public Task<List<Product>> ListWithConversionsAsync(IEnumerable<ProductId> ids, CancellationToken ct = default) =>
        Task.FromResult(Items.Where(p => ids.Contains(p.Id)).ToList());

    public Task<List<Product>> ListVariantsAsync(ProductId parentId, CancellationToken ct = default) =>
        Task.FromResult(Items.Where(p => p.ParentProductId == parentId).ToList());

    public Task AddAsync(Product product, CancellationToken ct = default)
    {
        Items.Add(product);
        // Also register the new variant so subsequent FindAsync for it works.
        _byId[product.Id] = product;
        _byName[product.Name.ToLowerInvariant()] = product;
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCalls++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeSingleUnitRepository(CatalogUnit unit) : IUnitRepository
{
    public Task<CatalogUnit?> FindAsync(UnitId id, CancellationToken ct = default) =>
        Task.FromResult(unit.Id == id ? (CatalogUnit?)unit : null);

    public Task<CatalogUnit?> FindByCodeAsync(string code, CancellationToken ct = default) =>
        Task.FromResult(unit.Code.Equals(code, StringComparison.OrdinalIgnoreCase) ? (CatalogUnit?)unit : null);

    public Task<List<CatalogUnit>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(new List<CatalogUnit> { unit });

    public Task AddAsync(CatalogUnit u, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakeEmptyCategoryRepository : ICategoryRepository
{
    public Task<Category?> FindAsync(CategoryId id, CancellationToken ct = default) => Task.FromResult<Category?>(null);
    public Task<Category?> FindByNameAsync(string name, CancellationToken ct = default) => Task.FromResult<Category?>(null);
    public Task<List<Category>> ListAsync(CancellationToken ct = default) => Task.FromResult(new List<Category>());
    public Task<List<Category>> ListActiveAsync(CancellationToken ct = default) => Task.FromResult(new List<Category>());
    public Task AddAsync(Category c, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakeEmptyLocationRepository : ILocationRepository
{
    public Task<Location?> FindAsync(LocationId id, CancellationToken ct = default) => Task.FromResult<Location?>(null);
    public Task<Location?> FindByNameAsync(string name, CancellationToken ct = default) => Task.FromResult<Location?>(null);
    public Task<List<Location>> ListAsync(CancellationToken ct = default) => Task.FromResult(new List<Location>());
    public Task<List<Location>> ListActiveAsync(CancellationToken ct = default) => Task.FromResult(new List<Location>());
    public Task AddAsync(Location l, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakeDetailStockRepository : IProductStockRepository
{
    internal List<ProductStock> Items { get; } = [];

    public Task<ProductStock?> FindAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(s => s.HouseholdId == householdId && s.ProductId == productId));

    public Task<ProductStock?> FindForUpdateAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default) =>
        FindAsync(householdId, productId, ct);

    public Task<ProductStock?> FindWithHistoryAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default) =>
        FindAsync(householdId, productId, ct);

    public Task<List<ProductStock>> ListForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(Items.Where(s => s.HouseholdId == householdId).ToList());

    public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(Items.Any(s => s.HouseholdId == householdId));

    public Task AddAsync(ProductStock stock, CancellationToken ct = default)
    {
        Items.Add(stock);
        return Task.CompletedTask;
    }

    public Task<bool> TryAddAndSaveAsync(ProductStock stock, CancellationToken ct = default)
    {
        Items.Add(stock);
        return Task.FromResult(true);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default) =>
        await work(ct);
}
