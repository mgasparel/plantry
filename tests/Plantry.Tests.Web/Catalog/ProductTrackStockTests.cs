using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.Inventory.Domain;
using Plantry.Tests.Web.Infrastructure;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Web.Catalog;

/// <summary>
/// L4 Web integration tests for the "Track stock" toggle on the Catalog product Create and Edit
/// screens (plantry-9ndg). Covers: Create defaults the checkbox checked and honours an explicit
/// unchecked post; Edit flips the flag in both directions; a parent product hides the toggle and
/// keeps its flag untouched no matter what an (unrendered-field) post carries.
///
/// The catalog / inventory seams are replaced by in-memory fakes (shared with the sibling
/// AddVariant / MakeVariantOptions Detail-page tests in this namespace); no database is touched.
/// </summary>
public sealed class ProductCreateTrackStockTests : IDisposable
{
    private static readonly Guid HouseholdId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private readonly ProductCreateTrackStockFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient AuthClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdId.ToString());
        return client;
    }

    private async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var html = await (await client.GetAsync("/Catalog/Products/Create")).Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the Create page.");
        return match.Groups[1].Value;
    }

    [Fact(DisplayName = "Create page — Track stock checkbox renders checked by default")]
    public async Task GetCreate_RendersTrackStockCheckbox_CheckedByDefault()
    {
        var client = AuthClient();

        var html = await (await client.GetAsync("/Catalog/Products/Create")).Content.ReadAsStringAsync();

        var match = Regex.Match(html, "<input[^>]*name=\"Input\\.TrackStock\"[^>]*>");
        Assert.True(match.Success, "The Track stock checkbox was not rendered on the Create page.");
        Assert.Contains("checked", match.Value);
        Assert.Contains("type=\"checkbox\"", match.Value);
    }

    [Fact(DisplayName = "Create — posting Track stock unchecked creates an untracked product")]
    public async Task PostCreate_TrackStockFalse_CreatesUntrackedProduct()
    {
        var client = AuthClient();
        var token = await GetAntiforgeryTokenAsync(client);

        var response = await client.PostAsync("/Catalog/Products/Create", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            new KeyValuePair<string, string>("Input.Name", "Table Salt"),
            new KeyValuePair<string, string>("Input.DefaultUnitId", _factory.UnitId.ToString()),
            new KeyValuePair<string, string>("Input.TrackStock", "false"),
        ]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var created = Assert.Single(_factory.ProductRepo.Items);
        Assert.Equal("Table Salt", created.Name);
        Assert.False(created.TrackStock);
    }

    [Fact(DisplayName = "Create — posting Track stock checked creates a tracked product")]
    public async Task PostCreate_TrackStockTrue_CreatesTrackedProduct()
    {
        var client = AuthClient();
        var token = await GetAntiforgeryTokenAsync(client);

        var response = await client.PostAsync("/Catalog/Products/Create", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            new KeyValuePair<string, string>("Input.Name", "Whole Milk"),
            new KeyValuePair<string, string>("Input.DefaultUnitId", _factory.UnitId.ToString()),
            new KeyValuePair<string, string>("Input.TrackStock", "true"),
        ]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var created = Assert.Single(_factory.ProductRepo.Items);
        Assert.Equal("Whole Milk", created.Name);
        Assert.True(created.TrackStock);
    }
}

/// <summary>
/// L4 Web integration tests for the "Track stock" toggle on the Product Detail (edit) page.
/// </summary>
public sealed class ProductDetailTrackStockTests : IDisposable
{
    private static readonly Guid HouseholdId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private readonly ProductDetailTrackStockFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient AuthClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdId.ToString());
        return client;
    }

    private async Task<string> GetAntiforgeryTokenAsync(HttpClient client, Guid productId)
    {
        var html = await (await client.GetAsync($"/Catalog/Products/{productId}")).Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the Detail page.");
        return match.Groups[1].Value;
    }

    private static FormUrlEncodedContent EditForm(string token, Product product, bool? trackStock) =>
        new(
        [
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            new KeyValuePair<string, string>("Input.Name", product.Name),
            new KeyValuePair<string, string>("Input.DefaultUnitId", product.DefaultUnitId.Value.ToString()),
            .. trackStock is { } ts
                ? new[] { new KeyValuePair<string, string>("Input.TrackStock", ts ? "true" : "false") }
                : [],
        ]);

    [Fact(DisplayName = "Edit — standalone product flips tracked to untracked")]
    public async Task Edit_TrackedProduct_FlipsToUntracked()
    {
        var client = AuthClient();
        var productId = _factory.TrackedStandaloneId;
        var token = await GetAntiforgeryTokenAsync(client, productId);
        var product = _factory.ProductRepo.Items.Single(p => p.Id.Value == productId);

        var response = await client.PostAsync(
            $"/Catalog/Products/{productId}",
            EditForm(token, product, trackStock: false));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(product.TrackStock);
    }

    [Fact(DisplayName = "Edit — standalone product flips untracked to tracked")]
    public async Task Edit_UntrackedProduct_FlipsToTracked()
    {
        var client = AuthClient();
        var productId = _factory.UntrackedStandaloneId;
        var token = await GetAntiforgeryTokenAsync(client, productId);
        var product = _factory.ProductRepo.Items.Single(p => p.Id.Value == productId);

        var response = await client.PostAsync(
            $"/Catalog/Products/{productId}",
            EditForm(token, product, trackStock: true));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(product.TrackStock);
    }

    [Fact(DisplayName = "Edit — parent product does not render the Track stock toggle")]
    public async Task Edit_ParentProduct_DoesNotRenderToggle()
    {
        var client = AuthClient();
        var productId = _factory.ParentId;

        var html = await (await client.GetAsync($"/Catalog/Products/{productId}")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("name=\"Input.TrackStock\"", html);
    }

    [Fact(DisplayName = "Edit — parent product POST preserves its existing Track stock flag")]
    public async Task Edit_ParentProduct_PostPreservesExistingFlag()
    {
        var client = AuthClient();
        var productId = _factory.ParentId;
        var token = await GetAntiforgeryTokenAsync(client, productId);
        var product = _factory.ProductRepo.Items.Single(p => p.Id.Value == productId);
        var before = product.TrackStock;

        // The form never rendered Input.TrackStock, so a real browser wouldn't post it either;
        // even if a value did arrive (e.g. a stale client), the command must ignore it for a parent.
        var response = await client.PostAsync(
            $"/Catalog/Products/{productId}",
            EditForm(token, product, trackStock: !before));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(before, product.TrackStock);
    }
}

// ── WAF factories ────────────────────────────────────────────────────────────

/// <summary>L4 factory for the Create-page Track stock tests. Empty repo; the command under
/// test persists new products into it.</summary>
internal sealed class ProductCreateTrackStockFactory : WebApplicationFactory<Program>
{
    internal FakeProductRepo ProductRepo { get; } = new();

    // Unit.Create always mints a fresh UnitId (no explicit-id overload) — capture the real
    // generated id rather than a made-up constant, or cross-ref validation 404s on it.
    internal Guid UnitId { get; private set; }

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

            var unit = CatalogUnit.Create(
                Plantry.SharedKernel.HouseholdId.From(Guid.Parse("cccccccc-0000-0000-0000-000000000001")),
                "ea", "Each", Dimension.Count, 1m, isBase: true);
            UnitId = unit.Id.Value;

            services.RemoveAll<IProductRepository>();
            services.AddScoped<IProductRepository>(_ => ProductRepo);

            services.RemoveAll<IUnitRepository>();
            services.AddSingleton<IUnitRepository>(new FakeSingleUnitRepository(unit));

            services.RemoveAll<ICategoryRepository>();
            services.AddSingleton<ICategoryRepository>(new FakeEmptyCategoryRepository());

            services.RemoveAll<ILocationRepository>();
            services.AddSingleton<ILocationRepository>(new FakeEmptyLocationRepository());
        });
    }
}

/// <summary>
/// L4 factory seeding three standalone/parent products keyed by their real domain ids: a tracked
/// standalone, an untracked standalone, and a parent (has a variant, so <c>IsParent</c> is true).
/// Reuses the in-memory fakes defined alongside the "Add a variant" Detail-page tests.
/// </summary>
internal sealed class ProductDetailTrackStockFactory : WebApplicationFactory<Program>
{
    internal FakeProductRepo ProductRepo { get; private set; } = new();
    internal Guid TrackedStandaloneId { get; private set; }
    internal Guid UntrackedStandaloneId { get; private set; }
    internal Guid ParentId { get; private set; }

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

            var household = Plantry.SharedKernel.HouseholdId.From(Guid.Parse("cccccccc-0000-0000-0000-000000000001"));
            var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
            var unit = CatalogUnit.Create(household, "ea", "Each", Dimension.Count, 1m, isBase: true);

            var tracked = Product.Create(household, "Whole Milk", unit.Id, clock, trackStock: true);
            var untracked = Product.Create(household, "Table Salt", unit.Id, clock, trackStock: false);
            var parent = Product.Create(household, "Bubly", unit.Id, clock);
            parent.SetHasVariants(true, clock);
            var parentVariant = Product.Create(household, "Bubly Blueberry Pomegranate", unit.Id, clock);
            parentVariant.MakeVariantOf(parent.Id, clock);

            TrackedStandaloneId = tracked.Id.Value;
            UntrackedStandaloneId = untracked.Id.Value;
            ParentId = parent.Id.Value;

            var productRepo = new FakeProductRepo();
            productRepo.AddWithId(tracked, TrackedStandaloneId);
            productRepo.AddWithId(untracked, UntrackedStandaloneId);
            productRepo.AddWithId(parent, ParentId);
            productRepo.AddWithId(parentVariant, parentVariant.Id.Value);
            ProductRepo = productRepo;

            services.RemoveAll<IProductRepository>();
            services.AddScoped<IProductRepository>(_ => ProductRepo);

            services.RemoveAll<IUnitRepository>();
            services.AddSingleton<IUnitRepository>(new FakeSingleUnitRepository(unit));

            services.RemoveAll<ICategoryRepository>();
            services.AddSingleton<ICategoryRepository>(new FakeEmptyCategoryRepository());

            services.RemoveAll<ILocationRepository>();
            services.AddSingleton<ILocationRepository>(new FakeEmptyLocationRepository());

            services.RemoveAll<IProductStockRepository>();
            services.AddSingleton<IProductStockRepository>(new FakeDetailStockRepository());

            services.RemoveAll<ProductQueryService>();
            services.AddScoped<ProductQueryService>();
        });
    }
}
