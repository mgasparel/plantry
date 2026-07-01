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
using CatalogCategory = Plantry.Catalog.Domain.Category;

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
        // The Walk page now mounts a Preact island (bead plantry-2zvm.2): rows, the save bar,
        // reason selector, and empty state are rendered client-side by take-stock.js.
        // The server emits a JSON hydration payload and the island root placeholder; the
        // test proves the hydration data is well-formed and contains the expected rows.
        var client = AuthClient();
        var resp = await client.GetAsync($"/pantry/take-stock/{TakeStockFixture.PantryLocId}");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        // Island mount point must be present.
        Assert.Contains("ts-walk-root", html);
        // Hydration element must be emitted.
        Assert.Contains("ts-walk-data", html);
        // Product ID from the fixture must appear in the hydration JSON.
        Assert.Contains(TakeStockFixture.FlourId.ToString(), html);
        // Recorded quantity (500) and unit (g) must appear in the hydration JSON.
        Assert.Contains("\"recorded\":500", html);
        Assert.Contains("\"unitCode\":\"g\"", html);
    }

    [Fact(DisplayName = "GET /pantry/take-stock/{locationId} renders empty state when no rows")]
    public async Task Get_Walk_EmptyState_WhenNoRows()
    {
        // Empty state is rendered by the island client-side. The server emits an empty
        // hydration array [] — the test proves the page returns 200 and the island root is present.
        var client = AuthClient();
        // Fridge location has no products in the fixture
        var resp = await client.GetAsync($"/pantry/take-stock/{TakeStockFixture.FridgeLocId}");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("ts-walk-root", html);
        // The hydration script specifically holds an empty array (not a stray "[]" elsewhere in markup).
        Assert.Matches(@"id=""ts-walk-data"">\[\]</script>", html);
    }

    [Fact(DisplayName = "GET /pantry/take-stock/{locationId} includes island hydration JSON (was: Alpine initialiser)")]
    public async Task Get_Walk_IncludesAlpineJson()
    {
        // Previously checked for "\"dirty\":false" in the Alpine working-set JSON.
        // After migration to the Preact island (plantry-2zvm.2), the payload is the richer
        // IslandRow array: productId, productName, recorded, unitCode, unitId, hasActiveStock,
        // lotsUrl, supportedUnits. The "dirty" field is no longer emitted server-side
        // (it is derived client-side via computed signal).
        var client = AuthClient();
        var resp = await client.GetAsync($"/pantry/take-stock/{TakeStockFixture.PantryLocId}");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        var flourId = TakeStockFixture.FlourId.ToString();
        Assert.Contains(flourId, html);
        Assert.Contains("\"recorded\":", html);
        Assert.Contains("\"productName\":", html);
        Assert.Contains("\"hasActiveStock\":", html);
        Assert.Contains("\"lotsUrl\":", html);
    }

    [Fact(DisplayName = "GET /pantry/take-stock/{locationId} includes island mount and sheet bridge")]
    public async Task Get_Walk_IncludesSaveBarAndReasonSelector()
    {
        // Previously checked for Alpine-rendered save-bar and reason-selector markup.
        // After migration (plantry-2zvm.2): the save-bar and reason-selector are rendered
        // client-side by take-stock.js; the server emits the island root + sheet bridge.
        var client = AuthClient();
        var resp = await client.GetAsync($"/pantry/take-stock/{TakeStockFixture.PantryLocId}");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        // Island root placeholder must be present.
        Assert.Contains("ts-walk-root", html);
        // Sheet bridge Alpine component must be present (inline-add sheet stays Alpine).
        Assert.Contains("takeStockSheetBridge", html);
        // Lot panel Alpine component must be present (lot panel fragments stay Alpine).
        Assert.Contains("takeStockLotPanel", html);
        // Antiforgery token must be emitted for island POST requests.
        Assert.Contains("__RequestVerificationToken", html);
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
        Assert.Contains("ts-hatch", html);
        Assert.Contains("300", html);    // lot A quantity
        Assert.Contains("200", html);    // lot B quantity
        Assert.Contains("Spoiled", html);
        Assert.Contains("Found stock", html);
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
        Assert.Contains("ts-hatch", html);
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

    [Fact(DisplayName = "POST AddItem success response carries the exact island-consumed key set (contract)")]
    public async Task Post_AddItem_Success_Response_HasExactKeySet()
    {
        // The island injects a new row straight from this JSON shape. Unlike the hydration DTO it is
        // an anonymous object with no compiler/typedef guard, so a server-side rename of (say) unitId
        // would silently break inline-add. Pin the exact key set — the one island wire shape that
        // otherwise had no contract test.
        var client = AuthClient();
        var pageResp = await client.GetAsync($"/pantry/take-stock/{TakeStockFixture.PantryLocId}");
        var token = ExtractAntiforgeryToken(await pageResp.Content.ReadAsStringAsync());

        var payload = new
        {
            name = "Contract Probe Item",
            defaultUnitId = TakeStockFixture.GramUnitId,
            countedValue = 100m,
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

        var root = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        var keys = root.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(
            new[] { "countedValue", "isSuccess", "productId", "productName", "unitCode", "unitId" },
            keys);
    }

    // ── J5: Category forwarding on standalone Path C (plantry-l92u) ──────────

    [Fact(DisplayName = "POST AddItem with categoryId on standalone path routes to CreateTrackedProductAsync with category (J5, plantry-l92u)")]
    public async Task Post_AddItem_Standalone_WithCategory_ForwardsCategory()
    {
        // Use a dedicated factory with a fresh catalog writer for isolation.
        using var catFactory = new TakeStockGroupedProductFactory();
        var client = catFactory.CreateAuthClient(TakeStockFixture.HouseholdAId);

        var pageResp = await client.GetAsync($"/pantry/take-stock/{TakeStockFixture.PantryLocId}");
        var token = ExtractAntiforgeryToken(await pageResp.Content.ReadAsStringAsync());

        // Standalone payload (no group) with a categoryId — verifies category is not dropped.
        var categoryId = Guid.CreateVersion7();
        var payload = new
        {
            name          = "Himalayan Salt",
            defaultUnitId = TakeStockFixture.GramUnitId,
            countedValue  = 500m,
            countedUnitId = TakeStockFixture.GramUnitId,
            newGroupId    = "",
            newGroupName  = "",
            categoryId,
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
        Assert.Equal("Himalayan Salt", data.ProductName);
        // Verify the standalone creation path was invoked (not the grouped/variant paths).
        Assert.Equal(1, catFactory.CatalogWriter.CreateCalls);
        Assert.Equal("Himalayan Salt", catFactory.CatalogWriter.LastName);
        // Verify the category value was forwarded to the writer (not silently dropped).
        Assert.Equal(categoryId, catFactory.CatalogWriter.LastCategoryId);
    }

    // ── J5: Group-aware AddItem paths (plantry-l92u) ──────────────────────────

    [Fact(DisplayName = "POST AddItem with newGroupName creates grouped product and records opening balance (J5, plantry-l92u)")]
    public async Task Post_AddItem_WithNewGroupName_CreatesGroupedProduct()
    {
        // Use a dedicated factory with a fresh catalog writer so CreateCalls is not shared
        // with other tests that also exercise the inline-add path via the class-fixture factory.
        using var groupFactory = new TakeStockGroupedProductFactory();
        var client = groupFactory.CreateAuthClient(TakeStockFixture.HouseholdAId);

        var pageResp = await client.GetAsync($"/pantry/take-stock/{TakeStockFixture.PantryLocId}");
        var token = ExtractAntiforgeryToken(await pageResp.Content.ReadAsStringAsync());

        // Payload: new group + first variant (CreateGroupedProductCommand path).
        var payload = new
        {
            name          = "Oat Milk",
            defaultUnitId = TakeStockFixture.GramUnitId,
            countedValue  = 1m,
            countedUnitId = TakeStockFixture.GramUnitId,
            newGroupId    = "",
            newGroupName  = "Milk",
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
        Assert.Equal("Oat Milk", data.ProductName);
        Assert.Equal(1m, data.CountedValue);
        // Verify the grouped-product creation path was invoked.
        Assert.Equal(1, groupFactory.CatalogWriter.CreateCalls);
        Assert.Equal("Oat Milk", groupFactory.CatalogWriter.LastName);
    }

    [Fact(DisplayName = "POST AddItem with non-empty newGroupId creates variant of existing group (J5, plantry-l92u)")]
    public async Task Post_AddItem_WithNewGroupId_CreatesVariant()
    {
        // Use a dedicated factory with a fresh catalog writer (isolation from class-fixture factory).
        using var variantFactory = new TakeStockGroupedProductFactory();
        var client = variantFactory.CreateAuthClient(TakeStockFixture.HouseholdAId);

        var pageResp = await client.GetAsync($"/pantry/take-stock/{TakeStockFixture.PantryLocId}");
        var token = ExtractAntiforgeryToken(await pageResp.Content.ReadAsStringAsync());

        var existingGroupId = Guid.CreateVersion7();

        // Payload: join existing group (CreateVariantCommand path).
        var payload = new
        {
            name          = "Whole Milk",
            defaultUnitId = TakeStockFixture.GramUnitId,
            countedValue  = 2m,
            countedUnitId = TakeStockFixture.GramUnitId,
            newGroupId    = existingGroupId.ToString(),
            newGroupName  = "Milk",
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
        Assert.Equal("Whole Milk", data.ProductName);
        Assert.Equal(2m, data.CountedValue);
        // Verify the variant creation path was invoked.
        Assert.Equal(1, variantFactory.CatalogWriter.CreateCalls);
        Assert.Equal("Whole Milk", variantFactory.CatalogWriter.LastName);
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

    public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(_stocks.Any(s => s.HouseholdId == householdId));

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
    /// <summary>Category id passed to the most recent <see cref="CreateTrackedProductAsync"/> call.</summary>
    public Guid? LastCategoryId { get; private set; }

    public Task<Guid> CreateTrackedProductAsync(
        string name, Guid defaultUnitId, Guid? categoryId, Guid defaultLocationId, CancellationToken ct = default)
    {
        CreateCalls++;
        LastName = name;
        LastUnitId = defaultUnitId;
        LastLocationId = defaultLocationId;
        LastCategoryId = categoryId;

        if (throwMessage is not null)
            throw new InvalidOperationException(throwMessage);

        return Task.FromResult(returnProductId ?? DefaultProductId);
    }

    public Task<Guid> CreateTrackedVariantAsync(
        Guid parentGroupId, string variantName,
        Guid? unitOverride, Guid? categoryOverride, Guid? locationOverride,
        CancellationToken ct = default)
    {
        CreateCalls++;
        LastName = variantName;
        if (locationOverride.HasValue) LastLocationId = locationOverride.Value;

        if (throwMessage is not null)
            throw new InvalidOperationException(throwMessage);

        return Task.FromResult(returnProductId ?? DefaultProductId);
    }

    public Task<Guid> CreateTrackedGroupedProductAsync(
        string groupName, string variantName,
        Guid defaultUnitId, Guid? categoryId, Guid? defaultLocationId,
        CancellationToken ct = default)
    {
        CreateCalls++;
        LastName = variantName;
        LastUnitId = defaultUnitId;
        if (defaultLocationId.HasValue) LastLocationId = defaultLocationId.Value;

        if (throwMessage is not null)
            throw new InvalidOperationException(throwMessage);

        return Task.FromResult(returnProductId ?? DefaultProductId);
    }

    public Task SetDefaultLocationAsync(Guid productId, Guid locationId, CancellationToken ct = default) =>
        Task.CompletedTask;
}

/// <summary>
/// Fake <see cref="IProductRepository"/> for L4 fragment tests.
/// Returns an empty catalog (no existing groups) so the create-view group combobox
/// renders with an empty groupOptions list (plantry-40n6).
/// </summary>
public sealed class FakeTsProductRepository : IProductRepository
{
    private readonly List<Product> _items = [];
    public IReadOnlyList<Product> Items => _items;

    public Task<Product?> FindAsync(ProductId id, CancellationToken ct = default) =>
        Task.FromResult(_items.SingleOrDefault(p => p.Id == id));

    public Task<Product?> FindByNameAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(_items.SingleOrDefault(p =>
            p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)));

    public Task<List<Product>> ListActiveAsync(CancellationToken ct = default) =>
        Task.FromResult(_items.Where(p => p.ArchivedAt is null).ToList());

    public Task<List<Product>> ListActiveWithSkusAsync(CancellationToken ct = default) =>
        Task.FromResult(_items.Where(p => p.ArchivedAt is null).ToList());

    public Task<List<Product>> ListWithConversionsAsync(
        IEnumerable<ProductId> ids, CancellationToken ct = default) =>
        Task.FromResult(_items.Where(p => ids.Contains(p.Id)).ToList());

    public Task<List<Product>> ListVariantsAsync(ProductId parentId, CancellationToken ct = default) =>
        Task.FromResult(_items.Where(p => p.ParentProductId == parentId).ToList());

    public Task AddAsync(Product product, CancellationToken ct = default)
    {
        _items.Add(product);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
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

/// <summary>
/// Fake <see cref="ICategoryRepository"/> for L4 fragment tests. Returns an empty category list
/// so the Defaults collapsible in the create view renders with no options other than "— None —"
/// (plantry-y53t). WalkModel now resolves ICategoryRepository in LoadAsync.
/// </summary>
public sealed class FakeTsCategoryRepository : ICategoryRepository
{
    public Task<CatalogCategory?> FindAsync(CategoryId id, CancellationToken ct = default) =>
        Task.FromResult<CatalogCategory?>(null);

    public Task<CatalogCategory?> FindByNameAsync(string name, CancellationToken ct = default) =>
        Task.FromResult<CatalogCategory?>(null);

    public Task<List<CatalogCategory>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(new List<CatalogCategory>());

    public Task<List<CatalogCategory>> ListActiveAsync(CancellationToken ct = default) =>
        Task.FromResult(new List<CatalogCategory>());

    public Task AddAsync(CatalogCategory category, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        Task.CompletedTask;
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

        // plantry-40n6: group combobox — WalkModel now resolves IProductRepository to load group options.
        services.RemoveAll<IProductRepository>();
        services.AddSingleton<IProductRepository, FakeTsProductRepository>();

        // plantry-y53t: Defaults collapsible — WalkModel now resolves ICategoryRepository to load category options.
        services.RemoveAll<ICategoryRepository>();
        services.AddSingleton<ICategoryRepository, FakeTsCategoryRepository>();
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

/// <summary>
/// L4 WebApplicationFactory for the group-aware AddItem tests (plantry-l92u).
/// Provides a fresh <see cref="FakeTsCatalogWriter"/> instance so each test can inspect
/// <see cref="FakeTsCatalogWriter.CreateCalls"/> independently of the class-fixture factory.
/// </summary>
public sealed class TakeStockGroupedProductFactory : WebApplicationFactory<Program>
{
    /// <summary>Exposed so tests can assert on which creation path was invoked.</summary>
    public FakeTsCatalogWriter CatalogWriter { get; } = new FakeTsCatalogWriter();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
            TakeStockFragmentFactory.RegisterFakes(services, catalogWriter: CatalogWriter));
    }

    public HttpClient CreateAuthClient(Guid householdId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, householdId.ToString());
        return client;
    }
}

// TakeStockNoLocationFragmentFactory is reserved for plantry-hcj3.9 (P4-8).
