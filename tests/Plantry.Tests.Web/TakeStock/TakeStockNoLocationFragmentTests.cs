using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Web.TakeStock;

/// <summary>
/// L4 fragment tests for the Take Stock "No location" page (P4-8, J7).
/// Uses the WAF harness with in-memory fake services — no Postgres touched.
///
/// Covers the L2 acceptance criteria from the ticket:
///   L2: count + location → opening-balance lot in that Location + default location set.
///   L2: 0-count + location → default location only, no lot.
///   L2: row leaves the list (client-side, proved by the Save response isSuccess=true).
///
/// Additional L4 coverage:
///   - GET /pantry/take-stock/no-location renders the product rows and location picker.
///   - GET /pantry/take-stock (index) shows "No location" entry when HasNoLocationProducts.
///   - GET /pantry/take-stock (index) omits "No location" entry when no unplaced products.
///   - Unauthenticated GET returns 401.
/// </summary>
public sealed class TakeStockNoLocationFragmentTests : IClassFixture<TakeStockNoLocationFactory>
{
    private readonly TakeStockNoLocationFactory _factory;

    public TakeStockNoLocationFragmentTests(TakeStockNoLocationFactory factory) => _factory = factory;

    private HttpClient AuthClient() =>
        _factory.CreateAuthClient(TakeStockFixture.HouseholdAId);

    // ── GET /pantry/take-stock/no-location ────────────────────────────────────

    [Fact(DisplayName = "GET /pantry/take-stock/no-location renders product rows with location picker")]
    public async Task Get_NoLocation_RendersRowsAndLocationPicker()
    {
        var client = AuthClient();
        var resp = await client.GetAsync("/pantry/take-stock/no-location");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        // The orphan product name from the fixture
        Assert.Contains("Orphan Product", html);
        // Location picker is present (a select per row)
        Assert.Contains("ts-locpick", html);
        Assert.Contains("Choose location", html);
        // Location options from FakeTakeStockReader.ListLocationsAsync
        Assert.Contains("Pantry", html);
        Assert.Contains("Fridge", html);
        // Save bar markup is rendered
        Assert.Contains("ts-savebar", html);
    }

    [Fact(DisplayName = "GET /pantry/take-stock/no-location renders empty state when no unplaced products")]
    public async Task Get_NoLocation_EmptyState_WhenNoUnplacedProducts()
    {
        // Use the standard factory (hasNoLocationProducts=false)
        using var emptyFactory = new TakeStockNoLocationEmptyFactory();
        var client = emptyFactory.CreateAuthClient(TakeStockFixture.HouseholdAId);

        var resp = await client.GetAsync("/pantry/take-stock/no-location");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("ts-empty", html);
    }

    // ── Index shows "No location" entry ──────────────────────────────────────

    [Fact(DisplayName = "GET /pantry/take-stock shows 'No location' entry when HasNoLocationProducts")]
    public async Task Get_Index_ShowsNoLocationEntry_WhenHasUnplacedProducts()
    {
        var client = AuthClient();
        var resp = await client.GetAsync("/pantry/take-stock");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("no-location", html);
        Assert.Contains("need filing", html);
    }

    [Fact(DisplayName = "GET /pantry/take-stock omits 'No location' entry when no unplaced products")]
    public async Task Get_Index_OmitsNoLocationEntry_WhenNoUnplacedProducts()
    {
        // Standard factory with no unplaced products
        using var stdFactory = new TakeStockNoLocationEmptyFactory();
        var client = stdFactory.CreateAuthClient(TakeStockFixture.HouseholdAId);
        var resp = await client.GetAsync("/pantry/take-stock");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        // The entry text is "need filing" (matched by the positive test above); asserting the
        // typo'd "needs filing" here could never fail. Use the real text so this guards anything.
        Assert.DoesNotContain("need filing", html);
    }

    // ── L2/L4: POST Save — count + location → lot + default set ──────────────

    [Fact(DisplayName = "L2: POST Save count+location → opening-balance lot + default set (J7)")]
    public async Task Post_Save_CountAndLocation_WritesLotAndDefaultLocation()
    {
        var client = AuthClient();

        // Get antiforgery token
        var pageResp = await client.GetAsync("/pantry/take-stock/no-location");
        var pageHtml = await pageResp.Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        // OrphanProduct (FlourId) with count=750g (> 500g seeded → delta +250g Up) and Pantry location
        var payload = new
        {
            items = new[]
            {
                new
                {
                    productId = TakeStockFixture.FlourId,
                    locationId = TakeStockFixture.PantryLocId,
                    countedValue = 750m,
                    countedUnitId = TakeStockFixture.GramUnitId,
                }
            }
        };

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/pantry/take-stock/no-location?handler=Save")
        {
            Content = JsonContent.Create(payload, options: new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            })
        };
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var resp = await client.SendAsync(request);
        resp.EnsureSuccessStatusCode();

        var data = await resp.Content.ReadFromJsonAsync<SaveResponse>();
        Assert.NotNull(data);
        var result = Assert.Single(data.Results);
        Assert.Equal(TakeStockFixture.FlourId, result.ProductId);
        Assert.True(result.IsSuccess, $"Expected success but got: {result.Error}");

        // Verify the catalog writer received the SetDefaultLocation call
        Assert.Equal(TakeStockFixture.FlourId, _factory.CatalogWriter.LastSetProductId);
        Assert.Equal(TakeStockFixture.PantryLocId, _factory.CatalogWriter.LastSetLocationId);
        Assert.Equal(1, _factory.CatalogWriter.SetLocationCalls);

        // Verify stock was written (opening-balance Correction lot).
        // Seeded stock has 500g at Pantry; count=750 → delta=+250g Correction.
        var stock = _factory.StockRepository.Items.SingleOrDefault(
            s => s.ProductId == TakeStockFixture.FlourId);
        Assert.NotNull(stock);
        var correctionJournals = stock.Journal
            .Where(j => j.Reason == StockReason.Correction && j.Delta > 0)
            .ToList();
        Assert.NotEmpty(correctionJournals);
        Assert.Contains(correctionJournals, j => j.Delta == 250m);
    }

    [Fact(DisplayName = "L2: POST Save 0-count + location → default location only, no lot (J7 edge)")]
    public async Task Post_Save_ZeroCountAndLocation_DefaultOnlyNoLot()
    {
        // Use a dedicated factory with an empty stock repository to cleanly assert no lot is added.
        using var zeroFactory = new TakeStockNoLocationZeroCountFactory();
        var client = zeroFactory.CreateAuthClient(TakeStockFixture.HouseholdAId);

        var pageResp = await client.GetAsync("/pantry/take-stock/no-location");
        var token = ExtractAntiforgeryToken(await pageResp.Content.ReadAsStringAsync());

        // 0-count — only SetDefaultLocation should fire, no RecordCountCommand
        var payload = new
        {
            items = new[]
            {
                new
                {
                    productId = TakeStockFixture.FlourId,
                    locationId = TakeStockFixture.FridgeLocId,
                    countedValue = 0m,
                    countedUnitId = TakeStockFixture.GramUnitId,
                }
            }
        };

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/pantry/take-stock/no-location?handler=Save")
        {
            Content = JsonContent.Create(payload, options: new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            })
        };
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var resp = await client.SendAsync(request);
        resp.EnsureSuccessStatusCode();

        var data = await resp.Content.ReadFromJsonAsync<SaveResponse>();
        Assert.NotNull(data);
        var result = Assert.Single(data.Results);
        Assert.True(result.IsSuccess, $"Expected success but got: {result.Error}");

        // SetDefaultLocation was called
        Assert.Equal(1, zeroFactory.CatalogWriter.SetLocationCalls);
        Assert.Equal(TakeStockFixture.FridgeLocId, zeroFactory.CatalogWriter.LastSetLocationId);

        // No stock root was created for a 0-count (no RecordCountCommand run).
        var stock = zeroFactory.StockRepository.Items.SingleOrDefault(
            s => s.ProductId == TakeStockFixture.FlourId);
        Assert.Null(stock);
    }

    [Fact(DisplayName = "POST Save with missing location returns per-item error (not 400)")]
    public async Task Post_Save_MissingLocation_ReturnsPerItemError()
    {
        var client = AuthClient();

        var pageResp = await client.GetAsync("/pantry/take-stock/no-location");
        var token = ExtractAntiforgeryToken(await pageResp.Content.ReadAsStringAsync());

        // No location supplied (Guid.Empty)
        var payload = new
        {
            items = new[]
            {
                new
                {
                    productId = TakeStockFixture.FlourId,
                    locationId = Guid.Empty,
                    countedValue = 10m,
                    countedUnitId = TakeStockFixture.GramUnitId,
                }
            }
        };

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/pantry/take-stock/no-location?handler=Save")
        {
            Content = JsonContent.Create(payload, options: new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            })
        };
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var resp = await client.SendAsync(request);
        resp.EnsureSuccessStatusCode(); // The handler returns 200 with isSuccess=false for missing-location

        var data = await resp.Content.ReadFromJsonAsync<SaveResponse>();
        Assert.NotNull(data);
        var result = Assert.Single(data.Results);
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    // ── Auth boundary ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "Unauthenticated GET /pantry/take-stock/no-location returns 401")]
    public async Task Unauthenticated_Get_Returns401()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var resp = await client.GetAsync("/pantry/take-stock/no-location");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on page.");
        return match.Groups[1].Value;
    }

    // ── Response DTOs ─────────────────────────────────────────────────────────

    private sealed class SaveResponse
    {
        public List<SaveResultItem> Results { get; set; } = [];
    }

    private sealed class SaveResultItem
    {
        public Guid    ProductId { get; set; }
        public bool    IsSuccess { get; set; }
        public string? Error     { get; set; }
    }
}

// ── Tracking fake catalog writer (P4-8) ──────────────────────────────────────

/// <summary>
/// Fake <see cref="ITakeStockCatalogWriter"/> that tracks <see cref="SetDefaultLocationAsync"/>
/// calls so L4 no-location tests can assert that the correct (product, location) pair was filed.
/// </summary>
public sealed class FakeTsNoLocationCatalogWriter : ITakeStockCatalogWriter
{
    public int    SetLocationCalls   { get; private set; }
    public Guid   LastSetProductId   { get; private set; }
    public Guid   LastSetLocationId  { get; private set; }

    public Task<Guid> CreateTrackedProductAsync(
        string name, Guid defaultUnitId, Guid? categoryId, Guid defaultLocationId, CancellationToken ct = default) =>
        Task.FromResult(Guid.NewGuid());

    public Task<Guid> CreateTrackedVariantAsync(
        Guid parentGroupId, string variantName,
        Guid? unitOverride, Guid? categoryOverride, Guid? locationOverride,
        CancellationToken ct = default) =>
        Task.FromResult(Guid.NewGuid());

    public Task<Guid> CreateTrackedGroupedProductAsync(
        string groupName, string variantName,
        Guid defaultUnitId, Guid? categoryId, Guid? defaultLocationId,
        CancellationToken ct = default) =>
        Task.FromResult(Guid.NewGuid());

    public Task SetDefaultLocationAsync(Guid productId, Guid locationId, CancellationToken ct = default)
    {
        SetLocationCalls++;
        LastSetProductId  = productId;
        LastSetLocationId = locationId;
        return Task.CompletedTask;
    }

    public Task AddConversionAsync(
        Guid productId, Guid fromUnitId, Guid toUnitId, decimal factor, CancellationToken ct = default) =>
        Task.CompletedTask;
}

// ── WAF factories ─────────────────────────────────────────────────────────────

/// <summary>
/// L4 WebApplicationFactory for the "No location" page (hasNoLocationProducts=true).
/// Exposes the tracking catalog writer and empty stock repo for assertion.
/// </summary>
public sealed class TakeStockNoLocationFactory : WebApplicationFactory<Program>
{
    /// <summary>Tracking catalog writer — asserts SetDefaultLocation was called.</summary>
    public FakeTsNoLocationCatalogWriter CatalogWriter { get; } = new FakeTsNoLocationCatalogWriter();

    /// <summary>Empty stock repository — asserts lots written by RecordCountCommand.</summary>
    public FakeTsStockRepository StockRepository { get; } = new FakeTsStockRepository();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            TakeStockFragmentFactory.RegisterFakes(
                services,
                stockRepo: StockRepository,
                catalogWriter: null,
                hasNoLocationProducts: true);

            // Override the catalog writer with our tracking version.
            services.RemoveAll<ITakeStockCatalogWriter>();
            services.AddSingleton<ITakeStockCatalogWriter>(CatalogWriter);
        });
    }

    public HttpClient CreateAuthClient(Guid householdId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(
            Infrastructure.TestAuthHandler.HouseholdHeader, householdId.ToString());
        return client;
    }
}

/// <summary>
/// L4 WAF factory for the No-location page with NO unplaced products (tests empty-state).
/// </summary>
public sealed class TakeStockNoLocationEmptyFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
            TakeStockFragmentFactory.RegisterFakes(services, hasNoLocationProducts: false));
    }

    public HttpClient CreateAuthClient(Guid householdId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(
            Infrastructure.TestAuthHandler.HouseholdHeader, householdId.ToString());
        return client;
    }
}

/// <summary>
/// L4 WAF factory for the 0-count path: uses an empty stock repository so that tests can assert
/// no lot was added for a zero-count file-this-product (J7 edge).
/// </summary>
public sealed class TakeStockNoLocationZeroCountFactory : WebApplicationFactory<Program>
{
    /// <summary>Empty stock repo — no pre-seeded lots.</summary>
    public FakeTsEmptyStockRepository StockRepository { get; } = new FakeTsEmptyStockRepository();

    /// <summary>Tracking catalog writer.</summary>
    public FakeTsNoLocationCatalogWriter CatalogWriter { get; } = new FakeTsNoLocationCatalogWriter();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            TakeStockFragmentFactory.RegisterFakes(services, hasNoLocationProducts: true);

            services.RemoveAll<IProductStockRepository>();
            services.AddSingleton<IProductStockRepository>(StockRepository);

            services.RemoveAll<ITakeStockCatalogWriter>();
            services.AddSingleton<ITakeStockCatalogWriter>(CatalogWriter);
        });
    }

    public HttpClient CreateAuthClient(Guid householdId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(
            Infrastructure.TestAuthHandler.HouseholdHeader, householdId.ToString());
        return client;
    }
}

/// <summary>
/// Empty (unseeded) fake stock repository for 0-count path tests.
/// No pre-existing lots — asserts that RecordCountCommand is never called for a 0-count save.
/// </summary>
public sealed class FakeTsEmptyStockRepository : IProductStockRepository
{
    private readonly List<ProductStock> _stocks = [];

    public IReadOnlyList<ProductStock> Items => _stocks;

    public Task<ProductStock?> FindAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default) =>
        Task.FromResult<ProductStock?>(_stocks.SingleOrDefault(s => s.HouseholdId == householdId && s.ProductId == productId));

    public Task<ProductStock?> FindForUpdateAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default) =>
        FindAsync(householdId, productId, ct);

    public Task<ProductStock?> FindWithHistoryAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default) =>
        FindAsync(householdId, productId, ct);

    public Task<List<ProductStock>> ListForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(_stocks.Where(s => s.HouseholdId == householdId).ToList());

    public Task AddAsync(ProductStock stock, CancellationToken ct = default)
    {
        _stocks.Add(stock);
        return Task.CompletedTask;
    }

    public Task<bool> TryAddAndSaveAsync(ProductStock stock, CancellationToken ct = default)
    {
        _stocks.Add(stock);
        return Task.FromResult(true);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(_stocks.Any(s => s.HouseholdId == householdId));

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        return await work(ct);
    }
}
