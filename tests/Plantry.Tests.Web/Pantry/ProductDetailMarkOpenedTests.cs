using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Catalog.Domain;
using Plantry.Identity.Application;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.Pricing.Application;
using Plantry.Pricing.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Web.Pantry;

/// <summary>
/// L4 Web integration tests for the "Mark opened" row action and its "Open" badge undo
/// (plantry-1le6, UI spec §1/§3) on the Pantry product Detail page. Unlike this page's other
/// mutations, these two handlers genuinely follow POST-Redirect-GET: an <c>HX-Redirect</c> response
/// header plus <c>TempData["ToastMessage"]</c> (the shared save-toast, plantry-u7n9/8b8802a) rather
/// than an OOB htmx fragment swap — this file proves that wiring end-to-end over HTTP, which the
/// pure-function tests in <c>MarkOpenedToastTests</c> cannot reach. Reuses the fake seams
/// <see cref="ProductDetailSetPriceTests"/> established for this page (unit/stock/pricing — those
/// internal fakes are file-scoped but assembly-visible) and adds its own lot-seeding + a Catalog facade
/// that can be configured with an after-opening default.
/// </summary>
public sealed class ProductDetailMarkOpenedTests : IDisposable
{
    private readonly ProductDetailMarkOpenedFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static readonly Guid HouseholdId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");

    private HttpClient AuthClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdId.ToString());
        return client;
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, Guid productId)
    {
        var html = await (await client.GetAsync($"/Pantry/Products/Detail/{productId}"))
            .Content.ReadAsStringAsync();

        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the Detail page.");
        return match.Groups[1].Value;
    }

    [Fact(DisplayName = "Detail GET — a sealed lot shows the 'Mark opened' action, not the Open badge")]
    public async Task Get_SealedLot_ShowsMarkOpenedAction()
    {
        var client = AuthClient();

        var response = await client.GetAsync($"/Pantry/Products/Detail/{ProductDetailMarkOpenedFixture.ProductId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Mark opened", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">Open<", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "MarkOpen — responds with HX-Redirect back to Detail and sets the toast")]
    public async Task MarkOpen_RespondsWithHxRedirect_AndSetsToast()
    {
        var client = AuthClient();
        var productId = ProductDetailMarkOpenedFixture.ProductId;
        var token = await GetAntiforgeryTokenAsync(client, productId);
        _factory.Catalog.DefaultDueDaysAfterOpening = 5;

        var response = await client.PostAsync(
            $"/Pantry/Products/Detail/{productId}?handler=MarkOpen&entryId={_factory.LotEntryId}",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("HX-Redirect"));
        Assert.Equal($"/Pantry/Products/Detail/{productId}", response.Headers.GetValues("HX-Redirect").Single());

        // Follow the redirect ourselves (the test client doesn't auto-follow a non-30x header) — the
        // toast rides in the TempData cookie the POST response set, same client instance.
        var follow = await client.GetAsync($"/Pantry/Products/Detail/{productId}");
        var html = await follow.Content.ReadAsStringAsync();
        Assert.Contains("Opened", html, StringComparison.Ordinal);
        Assert.Contains(">Open<", html, StringComparison.Ordinal); // the badge now shows instead of the action
    }

    [Fact(DisplayName = "UnmarkOpen — un-marks the lot and does not restore its pre-opening expiry")]
    public async Task UnmarkOpen_ClearsFlag_ToastDoesNotClaimRestoration()
    {
        var client = AuthClient();
        var productId = ProductDetailMarkOpenedFixture.ProductId;
        var token = await GetAntiforgeryTokenAsync(client, productId);
        _factory.Catalog.DefaultDueDaysAfterOpening = 5;

        await client.PostAsync(
            $"/Pantry/Products/Detail/{productId}?handler=MarkOpen&entryId={_factory.LotEntryId}",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));

        var response = await client.PostAsync(
            $"/Pantry/Products/Detail/{productId}?handler=UnmarkOpen&entryId={_factory.LotEntryId}",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("HX-Redirect"));
        Assert.False(_factory.Stock.Entries.Single().IsOpen);

        var follow = await client.GetAsync($"/Pantry/Products/Detail/{productId}");
        var html = await follow.Content.ReadAsStringAsync();
        Assert.Contains("Unmarked", html, StringComparison.Ordinal);
        Assert.Contains("Mark opened", html, StringComparison.Ordinal); // action is back
    }
}

// ── Fixture data ──────────────────────────────────────────────────────────────

internal static class ProductDetailMarkOpenedFixture
{
    internal static readonly Guid HouseholdId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    internal static readonly HouseholdId Household = Plantry.SharedKernel.HouseholdId.From(HouseholdId);
    internal static readonly IClock Clock = Plantry.SharedKernel.Domain.SystemClock.Instance;

    internal static readonly Guid ProductId = Guid.Parse("11111111-0000-0000-0000-111000000001");
    internal static readonly Guid UnitId = Guid.Parse("22222222-0000-0000-0000-222000000001");
    internal static readonly Guid LocationId = Guid.Parse("33333333-0000-0000-0000-333000000001");

    internal static CatalogUnit BuildUnit() =>
        CatalogUnit.Create(Household, "ea", "Each", Dimension.Count, 1m, isBase: true);
}

// ── WAF factory ───────────────────────────────────────────────────────────────

internal sealed class ProductDetailMarkOpenedFactory : WebApplicationFactory<Program>
{
    internal ProductStock Stock { get; private set; } = null!;
    internal StockEntryId LotEntryId { get; private set; }
    internal FakeOpeningDefaultCatalogFacade Catalog { get; } = new(ProductDetailMarkOpenedFixture.ProductId);

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

            var unit = ProductDetailMarkOpenedFixture.BuildUnit();

            services.RemoveAll<IUnitRepository>();
            services.AddSingleton<IUnitRepository>(new FakeSingleUnitRepository(unit));

            services.RemoveAll<ICatalogReadFacade>();
            services.AddSingleton<ICatalogReadFacade>(Catalog);

            Stock = ProductStock.Start(
                ProductDetailMarkOpenedFixture.Household, ProductDetailMarkOpenedFixture.ProductId,
                ProductDetailMarkOpenedFixture.Clock);
            var entry = Stock.AddStock(
                2m, ProductDetailMarkOpenedFixture.UnitId, ProductDetailMarkOpenedFixture.LocationId,
                Guid.NewGuid(), ProductDetailMarkOpenedFixture.Clock,
                expiryDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(90));
            LotEntryId = entry.Id;

            var stockRepo = new FakeDetailStockRepository();
            stockRepo.Items.Add(Stock);
            services.RemoveAll<IProductStockRepository>();
            services.AddSingleton<IProductStockRepository>(stockRepo);

            services.RemoveAll<IProductConversionProvider>();
            services.AddSingleton<IProductConversionProvider>(new IdentityConversionProvider());

            services.RemoveAll<IStockProvenanceReader>();
            services.AddSingleton<IStockProvenanceReader>(new FakeStockProvenanceReader());

            services.RemoveAll<IPriceObservationRepository>();
            services.AddSingleton<IPriceObservationRepository>(new FakePriceObservationRepository());

            services.RemoveAll<IDisplayCurrency>();
            services.AddSingleton<IDisplayCurrency>(new FakeDisplayCurrency());

            services.RemoveAll<IUnitPriceCalculator>();
            services.AddSingleton<IUnitPriceCalculator>(new FakeUnitPriceCalculator(0.5m));

            services.RemoveAll<Plantry.Recipes.Domain.IRecipeRepository>();
            services.AddSingleton<Plantry.Recipes.Domain.IRecipeRepository>(new FakeRecipeRepository());
        });
    }
}

/// <summary>A single-product <see cref="ICatalogReadFacade"/> whose after-opening default can be
/// mutated per-test (unlike <c>ProductDetailSetPriceTests</c>' fixed fake, which has no such field).</summary>
internal sealed class FakeOpeningDefaultCatalogFacade(Guid productId) : ICatalogReadFacade
{
    internal int? DefaultDueDaysAfterOpening { get; set; }

    public Task<CatalogProductInfo?> FindProductAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult<CatalogProductInfo?>(id == productId
            ? new CatalogProductInfo(
                productId, "Test Product", "Pantry", ProductDetailMarkOpenedFixture.UnitId, "ea",
                CanHoldStock: true, DefaultDueDaysAfterOpening: DefaultDueDaysAfterOpening)
            : null);

    public Task<IReadOnlyList<CatalogProductInfo>> ListProductsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogProductInfo>>([]);

    public Task<IReadOnlyDictionary<Guid, string>> GetUnitCodesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            new Dictionary<Guid, string> { [ProductDetailMarkOpenedFixture.UnitId] = "ea" });

    public Task<IReadOnlyDictionary<Guid, string>> GetLocationNamesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
}
