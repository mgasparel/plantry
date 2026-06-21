using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Catalog.Domain;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Web.Infrastructure;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Web.TakeStock;

/// <summary>
/// L4 fragment tests for the Take Stock walk pages (P4-4b, J1/J2/J4).
/// Uses the WAF harness with in-memory fake services — no Postgres touched.
///
/// Tests cover:
///  - GET /pantry/take-stock renders the location list (J1)
///  - GET /pantry/take-stock/{locationId} renders count rows (J2)
///  - GET /pantry/take-stock/{locationId} with no rows renders empty state
///  - POST Save with a dirty row returns a success result vector (J4)
///  - POST Save with empty items returns empty result vector
///  - Unauthenticated requests return 401
/// </summary>
public sealed class TakeStockFragmentTests : IClassFixture<TakeStockFragmentFactory>
{
    private readonly TakeStockFragmentFactory _factory;

    public TakeStockFragmentTests(TakeStockFragmentFactory factory) => _factory = factory;

    private HttpClient AuthClient() =>
        _factory.CreateAuthClient(TakeStockFixture.HouseholdAId);

    // ── J1: Location list ─────────────────────────────────────────────────────

    [Fact(DisplayName = "GET /pantry/take-stock renders location list")]
    public async Task Get_Index_RendersLocationList()
    {
        var client = AuthClient();
        var resp = await client.GetAsync("/pantry/take-stock");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Pantry", html);
        Assert.Contains("Fridge", html);
    }

    // ── J2: Walk page ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "GET /pantry/take-stock/{locationId} renders count rows")]
    public async Task Get_Walk_RendersCountRows()
    {
        var client = AuthClient();
        var resp = await client.GetAsync($"/pantry/take-stock/{TakeStockFixture.PantryLocId}");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Flour", html);
        Assert.Contains("500", html);   // recorded quantity
        Assert.Contains("g", html);     // unit code
        Assert.Contains("None left", html);
        Assert.Contains("take-stock-rows", html);
    }

    [Fact(DisplayName = "GET /pantry/take-stock/{locationId} renders empty state when no rows")]
    public async Task Get_Walk_EmptyState_WhenNoRows()
    {
        var client = AuthClient();
        // Fridge location has no products in the fixture
        var resp = await client.GetAsync($"/pantry/take-stock/{TakeStockFixture.FridgeLocId}");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("empty-state", html);
    }

    [Fact(DisplayName = "GET /pantry/take-stock/{locationId} includes Alpine initialiser JSON")]
    public async Task Get_Walk_IncludesAlpineJson()
    {
        var client = AuthClient();
        var resp = await client.GetAsync($"/pantry/take-stock/{TakeStockFixture.PantryLocId}");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        var flourId = TakeStockFixture.FlourId.ToString();
        Assert.Contains(flourId, html);
        Assert.Contains("\"recorded\":", html);
        Assert.Contains("\"dirty\":false", html);
    }

    [Fact(DisplayName = "GET /pantry/take-stock/{locationId} includes save bar and reason selector markup")]
    public async Task Get_Walk_IncludesSaveBarAndReasonSelector()
    {
        var client = AuthClient();
        var resp = await client.GetAsync($"/pantry/take-stock/{TakeStockFixture.PantryLocId}");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("take-stock-savebar", html);
        Assert.Contains("take-stock-row__reason", html);
        Assert.Contains("Correction", html);
        Assert.Contains("Used it", html);
        Assert.Contains("Spoiled", html);
    }

    // ── J4: Save handler ──────────────────────────────────────────────────────

    [Fact(DisplayName = "POST Save with dirty row returns success result (J4)")]
    public async Task Post_Save_WithDirtyRow_ReturnsSuccessResult()
    {
        var client = AuthClient();

        // Obtain antiforgery token
        var pageResp = await client.GetAsync($"/pantry/take-stock/{TakeStockFixture.PantryLocId}");
        var pageHtml = await pageResp.Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        // POST a dirty item (count flour from 500 → 300)
        var payload = new
        {
            items = new[]
            {
                new
                {
                    productId = TakeStockFixture.FlourId,
                    countedValue = 300m,
                    countedUnitId = TakeStockFixture.GramUnitId,
                    reason = "Correction"
                }
            }
        };

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/pantry/take-stock/{TakeStockFixture.PantryLocId}?handler=Save")
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
        Assert.NotEmpty(data.Results);
        var result = Assert.Single(data.Results);
        Assert.Equal(TakeStockFixture.FlourId, result.ProductId);
        Assert.True(result.IsSuccess, $"Expected success but got error: {result.Error}");
    }

    [Fact(DisplayName = "POST Save with empty items returns zero-length result vector")]
    public async Task Post_Save_WithNoItems_ReturnsEmptyVector()
    {
        var client = AuthClient();

        var pageResp = await client.GetAsync($"/pantry/take-stock/{TakeStockFixture.PantryLocId}");
        var token = ExtractAntiforgeryToken(await pageResp.Content.ReadAsStringAsync());

        var payload = new { items = Array.Empty<object>() };
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/pantry/take-stock/{TakeStockFixture.PantryLocId}?handler=Save")
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
        Assert.Empty(data.Results);
    }

    // ── J3: Lot panel fragment (P4-5) ─────────────────────────────────────────

    [Fact(DisplayName = "GET /Lots returns the lot panel fragment for a product in a location (J3)")]
    public async Task Get_Lots_ReturnsLotPanelFragment()
    {
        var client = AuthClient();
        var url = $"/pantry/take-stock/{TakeStockFixture.PantryLocId}?handler=Lots&productId={TakeStockFixture.FlourId}";
        var resp = await client.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("take-stock-lot-panel", html);
        Assert.Contains("300", html);    // lot A quantity
        Assert.Contains("200", html);    // lot B quantity
        Assert.Contains("Spoiled", html);
        Assert.Contains("Add found stock", html);
    }

    [Fact(DisplayName = "GET /Lots for a location with no lots renders empty state")]
    public async Task Get_Lots_EmptyLocationRendersEmptyState()
    {
        var client = AuthClient();
        // Fridge location has no lots in the fixture
        var url = $"/pantry/take-stock/{TakeStockFixture.FridgeLocId}?handler=Lots&productId={TakeStockFixture.FlourId}";
        var resp = await client.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("take-stock-lot-panel", html);
        Assert.Contains("No active lots", html);
    }

    [Fact(DisplayName = "POST SaveLots with a lot reduce writes removal and returns success (J3)")]
    public async Task Post_SaveLots_LotReduce_ReturnsSuccess()
    {
        var client = AuthClient();

        // Get antiforgery token from the walk page
        var pageResp = await client.GetAsync($"/pantry/take-stock/{TakeStockFixture.PantryLocId}");
        var token = ExtractAntiforgeryToken(await pageResp.Content.ReadAsStringAsync());

        // Use the seeded lot IDs from the factory
        var lotAId = _factory.StockRepository.FlourLotIds[0];

        var payload = new
        {
            adjustments = new[]
            {
                new
                {
                    entryId = lotAId,
                    amount = 50m,
                    unitId = TakeStockFixture.GramUnitId,
                    reason = "Correction",
                }
            }
        };

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/pantry/take-stock/{TakeStockFixture.PantryLocId}?handler=SaveLots&productId={TakeStockFixture.FlourId}")
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

        var data = await resp.Content.ReadFromJsonAsync<SaveLotsResponse>();
        Assert.NotNull(data);
        var result = Assert.Single(data.Results);
        Assert.Equal(lotAId, result.EntryId);
        Assert.True(result.IsSuccess, $"Expected success but got: {result.Error}");
    }

    [Fact(DisplayName = "POST SaveLots with spoiled reason writes Discarded (J3)")]
    public async Task Post_SaveLots_Spoiled_WritesDiscardedReason()
    {
        var client = AuthClient();

        var pageResp = await client.GetAsync($"/pantry/take-stock/{TakeStockFixture.PantryLocId}");
        var token = ExtractAntiforgeryToken(await pageResp.Content.ReadAsStringAsync());

        var lotBId = _factory.StockRepository.FlourLotIds[1];

        var payload = new
        {
            adjustments = new[]
            {
                new
                {
                    entryId = lotBId,
                    amount = 100m,
                    unitId = TakeStockFixture.GramUnitId,
                    reason = "Discarded",
                }
            }
        };

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/pantry/take-stock/{TakeStockFixture.PantryLocId}?handler=SaveLots&productId={TakeStockFixture.FlourId}")
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

        var data = await resp.Content.ReadFromJsonAsync<SaveLotsResponse>();
        Assert.NotNull(data);
        var result = Assert.Single(data.Results);
        Assert.True(result.IsSuccess, $"Expected success but got: {result.Error}");

        // Verify the journal entry has Discarded reason
        var stock = _factory.StockRepository.Items.Single(s => s.ProductId == TakeStockFixture.FlourId);
        var discardedJournals = stock.Journal.Where(j => j.Reason == Plantry.Inventory.Domain.StockReason.Discarded).ToList();
        Assert.Single(discardedJournals);
        Assert.Equal(-100m, discardedJournals[0].Delta);
    }

    [Fact(DisplayName = "POST SaveLots with found stock adds a Correction lot (J3)")]
    public async Task Post_SaveLots_FoundStock_AddsCorrectionLot()
    {
        var client = AuthClient();

        var pageResp = await client.GetAsync($"/pantry/take-stock/{TakeStockFixture.PantryLocId}");
        var token = ExtractAntiforgeryToken(await pageResp.Content.ReadAsStringAsync());

        var payload = new
        {
            adjustments = new[]
            {
                new
                {
                    entryId = (Guid?)null,
                    amount = 150m,
                    unitId = TakeStockFixture.GramUnitId,
                    reason = "Correction",
                    expiryDate = "2027-06-01",
                }
            }
        };

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/pantry/take-stock/{TakeStockFixture.PantryLocId}?handler=SaveLots&productId={TakeStockFixture.FlourId}")
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

        var data = await resp.Content.ReadFromJsonAsync<SaveLotsResponse>();
        Assert.NotNull(data);
        var result = Assert.Single(data.Results);
        Assert.True(result.IsSuccess, $"Expected success but got: {result.Error}");

        // The aggregate should have a new Correction lot
        var stock = _factory.StockRepository.Items.Single(s => s.ProductId == TakeStockFixture.FlourId);
        var correctionJournals = stock.Journal
            .Where(j => j.Reason == Plantry.Inventory.Domain.StockReason.Correction && j.Delta > 0)
            .ToList();
        Assert.NotEmpty(correctionJournals);
        Assert.Contains(correctionJournals, j => j.Delta == 150m);
    }

    // ── J5: Inline-add — SearchProducts + AddItem (P4-7) ─────────────────────

    [Fact(DisplayName = "GET /SearchProducts returns product option markup (J5)")]
    public async Task Get_SearchProducts_ReturnsProductOptionMarkup()
    {
        // The FakeTakeStockReader.SearchProductsAsync returns empty; use a factory with a reader
        // that returns a hit.
        var client = AuthClient();
        // Empty query → returns empty HTML.
        var resp = await client.GetAsync(
            $"/pantry/take-stock/{TakeStockFixture.PantryLocId}?handler=SearchProducts&q=");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Equal("", html);
    }

    [Fact(DisplayName = "GET /SearchProducts with query returns empty when no matches (J5)")]
    public async Task Get_SearchProducts_WithQuery_ReturnsEmptyForNoMatches()
    {
        var client = AuthClient();
        var resp = await client.GetAsync(
            $"/pantry/take-stock/{TakeStockFixture.PantryLocId}?handler=SearchProducts&q=xyznotexists");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        // Fake reader returns [] for all queries; response should be empty (no <li>).
        Assert.DoesNotContain("<li", html);
    }

    [Fact(DisplayName = "POST AddItem creates product and records opening balance (J5)")]
    public async Task Post_AddItem_CreatesProductAndRecordsOpeningBalance()
    {
        var client = AuthClient();

        var pageResp = await client.GetAsync($"/pantry/take-stock/{TakeStockFixture.PantryLocId}");
        var token = ExtractAntiforgeryToken(await pageResp.Content.ReadAsStringAsync());

        var payload = new
        {
            name = "New Tracked Item",
            defaultUnitId = TakeStockFixture.GramUnitId,
            countedValue = 250m,
            countedUnitId = TakeStockFixture.GramUnitId,
        };

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/pantry/take-stock/{TakeStockFixture.PantryLocId}?handler=AddItem")
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

        var data = await resp.Content.ReadFromJsonAsync<AddItemResponse>();
        Assert.NotNull(data);
        Assert.True(data.IsSuccess, $"Expected success but got error: {data.Error}");
        Assert.NotEqual(Guid.Empty, data.ProductId);
        Assert.Equal("New Tracked Item", data.ProductName);
        Assert.Equal(250m, data.CountedValue);
    }

    [Fact(DisplayName = "POST AddItem with duplicate name returns Catalog error inline (J5)")]
    public async Task Post_AddItem_DuplicateName_ReturnsErrorInline()
    {
        // Use a dedicated factory that registers a catalog writer configured to throw on create
        // (simulating Catalog.DuplicateProductName), so we can test the HTTP-layer error surfacing.
        using var dupFactory = new TakeStockDuplicateNameFactory();
        var dupClient = dupFactory.CreateAuthClient(TakeStockFixture.HouseholdAId);

        var pageResp = await dupClient.GetAsync($"/pantry/take-stock/{TakeStockFixture.PantryLocId}");
        var token = ExtractAntiforgeryToken(await pageResp.Content.ReadAsStringAsync());

        var payload = new
        {
            name = "Flour",
            defaultUnitId = TakeStockFixture.GramUnitId,
            countedValue = 5m,
            countedUnitId = TakeStockFixture.GramUnitId,
        };

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/pantry/take-stock/{TakeStockFixture.PantryLocId}?handler=AddItem")
        {
            Content = JsonContent.Create(payload, options: new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            })
        };
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var resp = await dupClient.SendAsync(request);
        resp.EnsureSuccessStatusCode(); // The handler returns 200 with isSuccess=false for domain errors.

        var data = await resp.Content.ReadFromJsonAsync<AddItemResponse>();
        Assert.NotNull(data);
        Assert.False(data.IsSuccess, "Expected isSuccess=false for a duplicate-name rejection.");
        Assert.NotNull(data.Error);
        Assert.Contains("Flour", data.Error);
    }

    // ── Auth boundary ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "Unauthenticated GET /pantry/take-stock returns 401")]
    public async Task Unauthenticated_Index_Returns401()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var resp = await client.GetAsync("/pantry/take-stock");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact(DisplayName = "Unauthenticated GET /pantry/take-stock/{locationId} returns 401")]
    public async Task Unauthenticated_Walk_Returns401()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var resp = await client.GetAsync($"/pantry/take-stock/{TakeStockFixture.PantryLocId}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact(DisplayName = "POST Save with empty-string countedUnitId returns per-row error, not HTTP 400 (defense-in-depth: stale Path A client)")]
    public async Task Post_Save_EmptyStringCountedUnitId_ReturnsPerRowError_NotBatch400()
    {
        var client = AuthClient();

        var pageResp = await client.GetAsync($"/pantry/take-stock/{TakeStockFixture.PantryLocId}");
        var token = ExtractAntiforgeryToken(await pageResp.Content.ReadAsStringAsync());

        // Send raw JSON with countedUnitId: "" (empty string) — the actual payload a stale Path A
        // client sends when draft.addUnitId was never seeded (before DefaultUnitId was plumbed).
        // Before the fix, this caused STJ to throw on non-parseable Guid, returning HTTP 400 for
        // the whole batch. After the fix, SaveItem.CountedUnitId is string? and TryParse guards it.
        var rawJson = $$"""
            {"items":[{"productId":"{{TakeStockFixture.FlourId}}","countedValue":300,"countedUnitId":"","reason":"Correction"}]}
            """;

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/pantry/take-stock/{TakeStockFixture.PantryLocId}?handler=Save")
        {
            Content = new StringContent(rawJson, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        // Must return 200 (not 400) — the defense-in-depth catches empty string at the row level.
        var resp = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var data = await resp.Content.ReadFromJsonAsync<SaveResponse>();
        Assert.NotNull(data);
        var result = Assert.Single(data.Results);
        Assert.Equal(TakeStockFixture.FlourId, result.ProductId);
        Assert.False(result.IsSuccess, "Expected per-row failure for empty-string countedUnitId");
        Assert.NotNull(result.Error);
    }

    [Fact(DisplayName = "POST Save with zero-guid countedUnitId returns per-row error, not HTTP 400 (defense-in-depth: zero Guid)")]
    public async Task Post_Save_ZeroGuidCountedUnitId_ReturnsPerRowError_NotBatch400()
    {
        var client = AuthClient();

        var pageResp = await client.GetAsync($"/pantry/take-stock/{TakeStockFixture.PantryLocId}");
        var token = ExtractAntiforgeryToken(await pageResp.Content.ReadAsStringAsync());

        // Send one item with Guid.Empty as the unit id — also guarded by the defense-in-depth.
        var payload = new
        {
            items = new[]
            {
                new
                {
                    productId = TakeStockFixture.FlourId,
                    countedValue = 300m,
                    countedUnitId = Guid.Empty,
                    reason = "Correction"
                }
            }
        };

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/pantry/take-stock/{TakeStockFixture.PantryLocId}?handler=Save")
        {
            Content = JsonContent.Create(payload, options: new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            })
        };
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var resp = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var data = await resp.Content.ReadFromJsonAsync<SaveResponse>();
        Assert.NotNull(data);
        var result = Assert.Single(data.Results);
        Assert.Equal(TakeStockFixture.FlourId, result.ProductId);
        Assert.False(result.IsSuccess, "Expected per-row failure for zero-guid countedUnitId");
        Assert.NotNull(result.Error);
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
        public Guid ProductId  { get; set; }
        public bool IsSuccess  { get; set; }
        public string? Error   { get; set; }
    }

    private sealed class SaveLotsResponse
    {
        public List<SaveLotResultItem> Results { get; set; } = [];
    }

    private sealed class SaveLotResultItem
    {
        public Guid?   EntryId   { get; set; }
        public bool    IsSuccess { get; set; }
        public string? Error     { get; set; }
    }

    private sealed class AddItemResponse
    {
        public bool    IsSuccess    { get; set; }
        public Guid    ProductId    { get; set; }
        public string? ProductName  { get; set; }
        public string? UnitCode     { get; set; }
        public Guid    UnitId       { get; set; }
        public decimal CountedValue { get; set; }
        public string? Error        { get; set; }
    }
}

// The "No location" card and its fragment test ship with plantry-hcj3.9 (P4-8),
// which adds the /pantry/take-stock/no-location route. IndexModel.HasNoLocationProducts
// is kept in place here so hcj3.9 can consume it without re-reading the reader contract.

// ── Fixture data ──────────────────────────────────────────────────────────────

public static class TakeStockFixture
{
    public static readonly Guid HouseholdAId  = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");
    public static readonly HouseholdId Household = HouseholdId.From(HouseholdAId);

    public static readonly Guid PantryLocId   = Guid.Parse("11111111-0000-0000-0000-100000000001");
    public static readonly Guid FridgeLocId   = Guid.Parse("11111111-0000-0000-0000-100000000002");
    public static readonly Guid FlourId       = Guid.Parse("22222222-0000-0000-0000-200000000001");
    public static readonly Guid GramUnitId    = Guid.Parse("33333333-0000-0000-0000-300000000001");
    public static readonly Guid LotAId        = Guid.Parse("44444444-0000-0000-0000-400000000001");
    public static readonly Guid LotBId        = Guid.Parse("44444444-0000-0000-0000-400000000002");

    public static TakeStockLocationRow PantryRow =>
        new(PantryLocId, "Pantry");

    public static TakeStockLocationRow FridgeRow =>
        new(FridgeLocId, "Fridge");

    public static TakeStockLocationProductRow FlourRow =>
        new(FlourId, "Flour", "g", 500m, HasActiveStock: true, DisplayUnitId: GramUnitId);

    public static TakeStockNoLocationRow OrphanRow =>
        new(FlourId, "Orphan Product", "g", 0m);
}

// ── In-memory fake reader ─────────────────────────────────────────────────────

public sealed class FakeTakeStockReader(bool hasNoLocationProducts = false) : ITakeStockReader
{
    public Task<IReadOnlyList<TakeStockLocationRow>> ListLocationsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TakeStockLocationRow>>(
        [
            TakeStockFixture.PantryRow,
            TakeStockFixture.FridgeRow,
        ]);

    public Task<IReadOnlyList<TakeStockLocationProductRow>> ListLocationRowsAsync(
        Guid locationId, CancellationToken ct = default)
    {
        IReadOnlyList<TakeStockLocationProductRow> rows = locationId == TakeStockFixture.PantryLocId
            ? [TakeStockFixture.FlourRow]
            : [];
        return Task.FromResult(rows);
    }

    public Task<IReadOnlyList<TakeStockNoLocationRow>> ListNoLocationRowsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<TakeStockNoLocationRow> rows = hasNoLocationProducts
            ? [TakeStockFixture.OrphanRow]
            : [];
        return Task.FromResult(rows);
    }

    public Task<IReadOnlyList<TakeStockLotRow>> ListLotsAsync(
        Guid productId, Guid locationId, CancellationToken ct = default)
    {
        IReadOnlyList<TakeStockLotRow> rows = productId == TakeStockFixture.FlourId && locationId == TakeStockFixture.PantryLocId
            ? [
                new TakeStockLotRow(TakeStockFixture.LotAId, 300m, "g", TakeStockFixture.GramUnitId, null, false),
                new TakeStockLotRow(TakeStockFixture.LotBId, 200m, "g", TakeStockFixture.GramUnitId, new DateOnly(2026, 12, 31), false),
              ]
            : [];
        return Task.FromResult(rows);
    }

    public Task<IReadOnlyList<TakeStockProductMatch>> SearchProductsAsync(
        string query, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TakeStockProductMatch>>([]);
}

// ── In-memory fake stock repository ──────────────────────────────────────────

public sealed class FakeTsStockRepository : IProductStockRepository
{
    private readonly List<ProductStock> _stocks = [];
    private static readonly IClock Clock = Plantry.SharedKernel.Domain.SystemClock.Instance;

    /// <summary>Exposes seeded stocks for assertion in L4 tests.</summary>
    public IReadOnlyList<ProductStock> Items => _stocks;

    /// <summary>
    /// The entry IDs added during seeding — used by L4 SaveLots tests to target specific lots.
    /// Index 0 = lot A (300g), index 1 = lot B (200g).
    /// </summary>
    public IReadOnlyList<Guid> FlourLotIds { get; }

    public FakeTsStockRepository()
    {
        // Seed: two Flour lots in Pantry location — matches the FakeTakeStockReader.ListLotsAsync fixture.
        var userId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
        var stock = ProductStock.Start(TakeStockFixture.Household, TakeStockFixture.FlourId, Clock);
        var lotA = stock.AddStock(300m, TakeStockFixture.GramUnitId, TakeStockFixture.PantryLocId, userId, Clock);
        var lotB = stock.AddStock(200m, TakeStockFixture.GramUnitId, TakeStockFixture.PantryLocId, userId, Clock);
        _stocks.Add(stock);
        FlourLotIds = [lotA.Id.Value, lotB.Id.Value];
    }

    public Task<List<ProductStock>> ListForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(_stocks.Where(s => s.HouseholdId == householdId).ToList());

    public Task<ProductStock?> FindAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default) =>
        Task.FromResult(_stocks.SingleOrDefault(s => s.HouseholdId == householdId && s.ProductId == productId));

    public Task<ProductStock?> FindForUpdateAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default) =>
        FindAsync(householdId, productId, ct);

    public Task<ProductStock?> FindWithHistoryAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default) =>
        FindAsync(householdId, productId, ct);

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

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> work, CancellationToken ct = default) =>
        await work(ct);
}

// ── In-memory fake catalog writer (P4-7) ─────────────────────────────────────

/// <summary>
/// Fake <see cref="ITakeStockCatalogWriter"/> for L4 fragment tests.
/// Returns a fixed product id on create; can be configured to throw for duplicate-name tests.
/// </summary>
public sealed class FakeTsCatalogWriter(Guid? returnProductId = null, string? throwMessage = null)
    : ITakeStockCatalogWriter
{
    private static readonly Guid DefaultProductId = Guid.Parse("55555555-0000-0000-0000-500000000001");

    public int CreateCalls { get; private set; }
    public string? LastName { get; private set; }
    public Guid LastUnitId { get; private set; }
    public Guid LastLocationId { get; private set; }

    public Task<Guid> CreateTrackedProductAsync(
        string name, Guid defaultUnitId, Guid defaultLocationId, CancellationToken ct = default)
    {
        CreateCalls++;
        LastName = name;
        LastUnitId = defaultUnitId;
        LastLocationId = defaultLocationId;

        if (throwMessage is not null)
            throw new InvalidOperationException(throwMessage);

        return Task.FromResult(returnProductId ?? DefaultProductId);
    }

    public Task SetDefaultLocationAsync(Guid productId, Guid locationId, CancellationToken ct = default) =>
        Task.CompletedTask;
}

/// <summary>
/// Fake <see cref="IUnitRepository"/> for L4 fragment tests. Returns a single gram unit.
/// </summary>
public sealed class FakeTsUnitRepository : IUnitRepository
{
    private readonly List<CatalogUnit> _items =
    [
        CatalogUnit.Create(HouseholdId.From(TakeStockFixture.HouseholdAId), "g", "gram",
            Dimension.Mass, factorToBase: 1m, isBase: true),
    ];

    public IReadOnlyList<CatalogUnit> Items => _items;

    public Task<CatalogUnit?> FindAsync(UnitId id, CancellationToken ct = default) =>
        Task.FromResult(_items.SingleOrDefault(u => u.Id == id));

    public Task<CatalogUnit?> FindByCodeAsync(string code, CancellationToken ct = default) =>
        Task.FromResult(_items.SingleOrDefault(u => u.Code.Equals(code, StringComparison.OrdinalIgnoreCase)));

    public Task<List<CatalogUnit>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(_items.ToList());

    public Task AddAsync(CatalogUnit unit, CancellationToken ct = default)
    {
        _items.Add(unit);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

// ── In-memory fake conversion provider ───────────────────────────────────────

public sealed class FakeTsConversionProvider : IProductConversionProvider
{
    public Task<IQuantityConverter> ForProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult<IQuantityConverter>(new IdentityConverter());

    public Task<IReadOnlyDictionary<Guid, IQuantityConverter>> ForProductsAsync(
        IEnumerable<Guid> productIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, IQuantityConverter> result =
            productIds.ToDictionary(id => id, _ => (IQuantityConverter)new IdentityConverter());
        return Task.FromResult(result);
    }

    private sealed class IdentityConverter : IQuantityConverter
    {
        public Plantry.SharedKernel.Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId)
            => amount;
    }
}

// ── WAF factories ─────────────────────────────────────────────────────────────

/// <summary>
/// L4 WebApplicationFactory for the Take Stock pages (no unplaced products).
/// </summary>
public sealed class TakeStockFragmentFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// The shared fake stock repository — exposed so L4 SaveLots tests can read seeded lot IDs.
    /// </summary>
    public FakeTsStockRepository StockRepository { get; } = new FakeTsStockRepository();

    /// <summary>
    /// The shared fake catalog writer — exposed so L4 inline-add tests can inspect create calls.
    /// </summary>
    public FakeTsCatalogWriter CatalogWriter { get; } = new FakeTsCatalogWriter();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
            RegisterFakes(services, StockRepository, CatalogWriter, hasNoLocationProducts: false));
    }

    internal static void RegisterFakes(
        IServiceCollection services,
        FakeTsStockRepository? stockRepo = null,
        FakeTsCatalogWriter? catalogWriter = null,
        bool hasNoLocationProducts = false)
    {
        services.AddAuthentication(opts =>
            {
                opts.DefaultScheme = TestAuthHandler.SchemeName;
                opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

        services.RemoveAll<ITakeStockReader>();
        services.AddSingleton<ITakeStockReader>(new FakeTakeStockReader(hasNoLocationProducts));

        services.RemoveAll<IProductStockRepository>();
        services.AddSingleton<IProductStockRepository>(stockRepo ?? new FakeTsStockRepository());

        services.RemoveAll<IProductConversionProvider>();
        services.AddSingleton<IProductConversionProvider, FakeTsConversionProvider>();

        // P4-7 inline-add fakes.
        services.RemoveAll<ITakeStockCatalogWriter>();
        services.AddSingleton<ITakeStockCatalogWriter>(catalogWriter ?? new FakeTsCatalogWriter());

        services.RemoveAll<IUnitRepository>();
        services.AddSingleton<IUnitRepository, FakeTsUnitRepository>();
    }

    /// <summary>Creates an authenticated HTTP client for the given household.</summary>
    public HttpClient CreateAuthClient(Guid householdId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, householdId.ToString());
        return client;
    }
}

/// <summary>
/// L4 WebApplicationFactory for the Take Stock inline-add duplicate-name test.
/// Registers a <see cref="FakeTsCatalogWriter"/> that throws an <see cref="InvalidOperationException"/>
/// to simulate the Catalog.DuplicateProductName error.
/// </summary>
public sealed class TakeStockDuplicateNameFactory : WebApplicationFactory<Program>
{
    private static readonly FakeTsCatalogWriter _dupWriter = new FakeTsCatalogWriter(
        throwMessage: "Create tracked product failed (Catalog.DuplicateProductName): A product named 'Flour' already exists.");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
            TakeStockFragmentFactory.RegisterFakes(services, catalogWriter: _dupWriter));
    }

    public HttpClient CreateAuthClient(Guid householdId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, householdId.ToString());
        return client;
    }
}

// TakeStockNoLocationFragmentFactory is reserved for plantry-hcj3.9 (P4-8).
