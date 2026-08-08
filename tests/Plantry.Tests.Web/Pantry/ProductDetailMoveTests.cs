using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Pantry.Domain;
using Plantry.Identity.Application;
using Plantry.Pantry.Application;
using Plantry.Market.Application;
using Plantry.Market.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;
using CatalogUnit = Plantry.Pantry.Domain.Unit;

namespace Plantry.Tests.Web.Pantry;

/// <summary>
/// L4 Web integration tests for the Move sheet (plantry-6owm) on the Pantry product Detail page — the
/// per-lot transfer/freeze/thaw entry point beside Use/Discard. Reuses the fake seams
/// <c>ProductDetailSetPriceTests</c> established for this page (unit/stock/pricing/recipes fakes are
/// assembly-visible within this namespace) and adds a two-location Catalog facade
/// (<see cref="FakeMoveCatalogFacade"/>, fridge + freezer) plus a matching <see cref="ILocationRepository"/>
/// fake so the destination picker has something to render. The transition math itself (freeze/thaw
/// recompute, split, guards) is covered at the domain/application layers in
/// <c>ProductStockTransferTests</c>/<c>TransferStockCommandTests</c> — this file proves the sheet
/// renders correctly and the handler wiring (OOB swap on success, model error on rejection) works
/// end-to-end over HTTP.
/// </summary>
public sealed class ProductDetailMoveTests : IDisposable
{
    private readonly ProductDetailMoveFactory _factory = new();

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

    [Fact(DisplayName = "MoveSheet — renders the destination picker with the ❄ suffix and the current location disabled")]
    public async Task MoveSheet_Renders_DestinationPicker()
    {
        var client = AuthClient();

        var response = await client.GetAsync(
            $"/Pantry/Products/Detail/{ProductDetailMoveFixture.ProductId}?handler=MoveSheet&entryId={_factory.LotEntryId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Move stock", html, StringComparison.Ordinal);
        // Razor HTML-encodes the ❄ glyph as a numeric entity — assert the decoded option text instead
        // of the raw character so this doesn't depend on the encoder's exact entity choice.
        var freezerOption = Regex.Match(html, "<option value=\"[^\"]+\"[^>]*>([^<]*)</option>\\s*</select>");
        Assert.True(freezerOption.Success, "Could not find the last <option> in the destination select.");
        Assert.Equal("Chest freezer ❄", System.Net.WebUtility.HtmlDecode(freezerOption.Groups[1].Value));
        Assert.Contains("Fridge (current)", html, StringComparison.Ordinal);
        Assert.Contains("disabled", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "MoveSheet — the Alpine-labelled submit button ships with non-empty server-rendered fallback text (plantry-5c5i)")]
    public async Task MoveSheet_SubmitButton_HasNonEmptyFallbackText()
    {
        var client = AuthClient();

        var response = await client.GetAsync(
            $"/Pantry/Products/Detail/{ProductDetailMoveFixture.ProductId}?handler=MoveSheet&entryId={_factory.LotEntryId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        // Content-based assertion, not just x-text attribute presence: a JS failure (error, CSP block)
        // must not leave the primary action button with an empty accessible name. Match the whole
        // submit button element (non-greedy up to its closing tag) so this catches an empty body even
        // if the x-text attribute itself is present.
        var button = Regex.Match(html, "<button type=\"submit\" class=\"btn btn--primary\" x-text=\"confirmLabel\">([^<]*)</button>");
        Assert.True(button.Success, "Could not find the Move sheet's submit button.");
        Assert.False(string.IsNullOrWhiteSpace(button.Groups[1].Value), "Submit button has no server-rendered fallback text.");
        Assert.StartsWith("Move ", button.Groups[1].Value, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "MoveSheet — a never-thawed lot's sheet emits hasThawedAt:false (refreeze warning gated off)")]
    public async Task MoveSheet_NeverThawedLot_EmitsHasThawedAtFalse()
    {
        var client = AuthClient();

        var response = await client.GetAsync(
            $"/Pantry/Products/Detail/{ProductDetailMoveFixture.ProductId}?handler=MoveSheet&entryId={_factory.LotEntryId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("hasThawedAt: false", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "MoveSheet — a product with no per-product override renders the household-default freeze note without claiming it's from this product (plantry-hh1f)")]
    public async Task MoveSheet_NoProductOverride_RendersHouseholdDefaultNote_NotFromThisProduct()
    {
        var client = AuthClient();
        // FakeMoveCatalogFacade's DefaultDueDaysAfterFreezing is unset (null) by default — the resolved
        // value it carries here is what CatalogReadFacade would have already resolved through the
        // household default (plantry-hh1f), so from this page's perspective it's just "the resolved
        // due-days"; either way, the copy must not claim it came from the product.
        _factory.Catalog.DefaultDueDaysAfterFreezing = 90;
        _factory.Catalog.DefaultDueDaysAfterThawing = 3;

        var response = await client.GetAsync(
            $"/Pantry/Products/Detail/{ProductDetailMoveFixture.ProductId}?handler=MoveSheet&entryId={_factory.LotEntryId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("90-day after-freezing default, counted from today.", html, StringComparison.Ordinal);
        Assert.Contains("3-day after-thawing default, counted from today.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("from this product", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "MoveSheet — string x-data fields are HTML-encoded so the attribute is not truncated (plantry-gcpb)")]
    public async Task MoveSheet_XData_IsHtmlEncoded_AndAttributeSpansWholeObject()
    {
        var client = AuthClient();

        var response = await client.GetAsync(
            $"/Pantry/Products/Detail/{ProductDetailMoveFixture.ProductId}?handler=MoveSheet&entryId={_factory.LotEntryId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        // A string-valued seed must arrive HTML-encoded — a raw `"` would terminate the
        // x-data="..." attribute early (the plantry-gcpb regression).
        Assert.Contains("unit: &quot;kg&quot;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("unit: \"kg\"", html, StringComparison.Ordinal);

        // The attribute value must span the *entire* object literal: [^"]* stops at the first
        // raw double quote, so this only matches if nothing truncated it mid-object.
        var xData = Regex.Match(html, "x-data=\"([^\"]*)\"");
        Assert.True(xData.Success, "No x-data attribute found on the Move sheet.");
        Assert.Contains("bumpQty(delta)", xData.Groups[1].Value, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "MoveSheet — a previously-thawed lot's sheet emits hasThawedAt:true, gating the refreeze warning on (UI spec §5)")]
    public async Task MoveSheet_ThawedLot_EmitsHasThawedAtTrue()
    {
        var client = AuthClient();

        // Simulate a prior freeze-then-thaw cycle directly on the domain aggregate: add a lot into
        // the freezer, then thaw it into the fridge via ProductStock.Transfer. This leaves ThawedAt
        // set while the lot's current location is non-frozen — exactly the state the refreeze
        // warning (UI spec §5) keys off, and a scenario the shared fixture lot (never transitioned)
        // cannot exercise.
        var thawedLot = _factory.Stock.AddStock(
            1m, ProductDetailMoveFixture.UnitId, ProductDetailMoveFixture.FreezerId,
            Guid.NewGuid(), ProductDetailMoveFixture.Clock,
            expiryDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30));
        var thawResult = _factory.Stock.TransferWithoutCatalogProduct(
            thawedLot.Id, ProductDetailMoveFixture.FridgeId, sourceIsFrozen: true, destinationIsFrozen: false,
            quantity: 1m, ProductDetailMoveFixture.Clock);
        Assert.True(thawResult.IsSuccess);

        var response = await client.GetAsync(
            $"/Pantry/Products/Detail/{ProductDetailMoveFixture.ProductId}?handler=MoveSheet&entryId={thawedLot.Id.Value}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("hasThawedAt: true", html, StringComparison.Ordinal);
        Assert.Contains("Refreezing previously thawed", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Move — full-lot freeze recomputes expiry, sets FrozenAt, and moves the lot's location")]
    public async Task Move_FullLot_Freeze_RecomputesExpiry()
    {
        var client = AuthClient();
        var productId = ProductDetailMoveFixture.ProductId;
        var token = await GetAntiforgeryTokenAsync(client, productId);
        var originalExpiry = _factory.Stock.Entries.Single().ExpiryDate;
        _factory.Catalog.DefaultDueDaysAfterFreezing = 90;

        var response = await client.PostAsync(
            $"/Pantry/Products/Detail/{productId}?handler=Move",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("MoveInput.EntryId", _factory.LotEntryId.Value.ToString()),
                new KeyValuePair<string, string>("MoveInput.LocationId", ProductDetailMoveFixture.FreezerId.ToString()),
                new KeyValuePair<string, string>("MoveInput.Quantity", "2"),
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("hx-swap-oob", html, StringComparison.Ordinal);

        var lot = _factory.Stock.Entries.Single();
        Assert.Equal(ProductDetailMoveFixture.FreezerId, lot.LocationId);
        Assert.NotNull(lot.FrozenAt);
        // Exact date math (extends past the near 5-days-out printed date) is covered by
        // ProductStockTransferTests/TransferStockCommandTests with a FixedClock; here we just prove the
        // recompute actually fired through the full HTTP round trip.
        Assert.NotEqual(originalExpiry, lot.ExpiryDate);
    }

    [Fact(DisplayName = "Move — destination equal to the current location re-renders the sheet with a model error and changes nothing")]
    public async Task Move_SameLocation_ReturnsModelError()
    {
        var client = AuthClient();
        var productId = ProductDetailMoveFixture.ProductId;
        var token = await GetAntiforgeryTokenAsync(client, productId);
        var originalLocation = _factory.Stock.Entries.Single().LocationId;

        var response = await client.PostAsync(
            $"/Pantry/Products/Detail/{productId}?handler=Move",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("MoveInput.EntryId", _factory.LotEntryId.Value.ToString()),
                new KeyValuePair<string, string>("MoveInput.LocationId", ProductDetailMoveFixture.FridgeId.ToString()),
                new KeyValuePair<string, string>("MoveInput.Quantity", "2"),
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Move stock", html, StringComparison.Ordinal); // sheet re-rendered, not the grid
        Assert.Equal(originalLocation, _factory.Stock.Entries.Single().LocationId);
    }
}

// ── Fixture data ──────────────────────────────────────────────────────────────

internal static class ProductDetailMoveFixture
{
    internal static readonly Guid HouseholdId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    internal static readonly HouseholdId Household = Plantry.SharedKernel.HouseholdId.From(HouseholdId);
    internal static readonly IClock Clock = Plantry.SharedKernel.Domain.SystemClock.Instance;

    internal static readonly Guid ProductId = Guid.Parse("11111111-0000-0000-0000-111000000002");
    internal static readonly Guid UnitId = Guid.Parse("22222222-0000-0000-0000-222000000002");

    // Location.Create mints its own id (no overload accepts one), so these two shared instances are
    // the single source of truth for "Fridge"/"Chest freezer" — FridgeId/FreezerId derive from them
    // rather than being independently-declared constants, keeping the lot's raw LocationId (AddStock),
    // the Catalog facade's dictionaries, and the ILocationRepository fake all pointing at the same ids.
    internal static readonly Location Fridge = Location.Create(Household, "Fridge", LocationType.Ambient);
    internal static readonly Location Freezer = Location.Create(Household, "Chest freezer", LocationType.Frozen);
    internal static Guid FridgeId => Fridge.Id.Value;
    internal static Guid FreezerId => Freezer.Id.Value;

    internal static CatalogUnit BuildUnit() =>
        CatalogUnit.Create(Household, "kg", "Kilograms", Dimension.Mass, 1m, isBase: true);
}

// ── WAF factory ───────────────────────────────────────────────────────────────

internal sealed class ProductDetailMoveFactory : WebApplicationFactory<Program>
{
    internal ProductStock Stock { get; private set; } = null!;
    internal StockEntryId LotEntryId { get; private set; }
    internal FakeMoveCatalogFacade Catalog { get; } = new(ProductDetailMoveFixture.ProductId);

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

            var unit = ProductDetailMoveFixture.BuildUnit();

            services.RemoveAll<IUnitRepository>();
            services.AddSingleton<IUnitRepository>(new FakeSingleUnitRepository(unit));

            services.RemoveAll<ICatalogReadFacade>();
            services.AddSingleton<ICatalogReadFacade>(Catalog);

            services.RemoveAll<ILocationRepository>();
            var moveLocations = new FakeLocationRepository();
            moveLocations.Seed(ProductDetailMoveFixture.Fridge);
            moveLocations.Seed(ProductDetailMoveFixture.Freezer);
            services.AddSingleton<ILocationRepository>(moveLocations);

            Stock = ProductStock.Start(
                ProductDetailMoveFixture.Household, ProductDetailMoveFixture.ProductId,
                ProductDetailMoveFixture.Clock);
            var entry = Stock.AddStock(
                2m, ProductDetailMoveFixture.UnitId, ProductDetailMoveFixture.FridgeId,
                Guid.NewGuid(), ProductDetailMoveFixture.Clock,
                expiryDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5));
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

/// <summary>A two-location (fridge/freezer) <see cref="ICatalogReadFacade"/> whose after-freezing/
/// after-thawing defaults can be mutated per-test.</summary>
internal sealed class FakeMoveCatalogFacade(Guid productId) : ICatalogReadFacade
{
    // The real Catalog adapter always resolves an existing product to an explicit policy by
    // applying the household fallback. Keep the fake at that contract boundary as well.
    internal int? DefaultDueDaysAfterFreezing { get; set; } = 90;
    internal int? DefaultDueDaysAfterThawing { get; set; } = 3;

    public Task<CatalogProductInfo?> FindProductAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult<CatalogProductInfo?>(id == productId
            ? new CatalogProductInfo(
                productId, "Test Product", "Pantry", ProductDetailMoveFixture.UnitId, "kg",
                CanHoldStock: true,
                AfterFreezingPolicy: DefaultDueDaysAfterFreezing is { } f ? new ExpiryTransitionPolicy.Days(f) : null,
                AfterThawingPolicy: DefaultDueDaysAfterThawing is { } t ? new ExpiryTransitionPolicy.Days(t) : null)
            : null);

    public Task<IReadOnlyList<CatalogProductInfo>> ListProductsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogProductInfo>>([]);

    public Task<IReadOnlyDictionary<Guid, string>> GetUnitCodesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            new Dictionary<Guid, string> { [ProductDetailMoveFixture.UnitId] = "kg" });

    public Task<IReadOnlyDictionary<Guid, string>> GetLocationNamesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>
        {
            [ProductDetailMoveFixture.FridgeId] = "Fridge",
            [ProductDetailMoveFixture.FreezerId] = "Chest freezer",
        });

    public Task<IReadOnlyDictionary<Guid, bool>> GetLocationFrozenFlagsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, bool>>(new Dictionary<Guid, bool>
        {
            [ProductDetailMoveFixture.FridgeId] = false,
            [ProductDetailMoveFixture.FreezerId] = true,
        });
}

