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
/// L4 Web integration tests for the parent product detail page (plantry-oh27.7): aggregate on-hand
/// across live variants, the per-variant breakdown, the low-stock threshold sheet enabled on a parent,
/// the price-history sparkline rolled up via <see cref="PriceHistoryRollup"/>, and the "Add stock to a
/// variant" picker replacing Consume/Add stock. Reuses the internal fakes
/// <see cref="ProductDetailSetPriceTests"/> established for this page's parent-price rollup
/// (<see cref="FakeDetailStockRepository"/>, <see cref="IdentityConversionProvider"/>,
/// <see cref="FakeParentProductRepository"/>, <see cref="FakePriceObservationRepository"/>, etc. — same
/// namespace) and adds only the one seam none of the existing fixtures cover: a catalog facade whose
/// <c>ListProductsAsync</c> actually lists the parent AND its live variants, which
/// <see cref="IOnHandRollupQuery"/> depends on to fold variants by <c>ParentProductId</c>.
/// </summary>
public sealed class ProductDetailParentTests : IDisposable
{
    private readonly ProductDetailParentFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient AuthClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ProductDetailParentFixture.HouseholdId.ToString());
        return client;
    }

    private async Task<string> GetAntiforgeryTokenAsync(HttpClient client, Guid productId)
    {
        var html = await (await client.GetAsync($"/Pantry/Products/Detail/{productId}")).Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the Detail page.");
        return match.Groups[1].Value;
    }

    /// <summary>Slices out the vitals strip's On-hand tile (<c>id="product-total"</c>,
    /// <c>_VitalsTiles.cshtml</c> line 20) so an assertion on the rendered aggregate can't be satisfied by
    /// an unrelated "4"/"10" elsewhere on the page (the antiforgery token, a variant's own row, etc).</summary>
    private static string OnHandTile(string html)
    {
        var i = html.IndexOf("id=\"product-total\"", StringComparison.Ordinal);
        Assert.True(i >= 0, "On-hand vitals tile not found.");
        var end = html.IndexOf("id=\"product-next-expiry\"", i, StringComparison.Ordinal);
        return html[i..(end < 0 ? html.Length : end)];
    }

    [Fact(DisplayName = "Detail GET — parent aggregates on-hand across live variants with a per-variant breakdown")]
    public async Task Get_Parent_RendersAggregateOnHandAndVariantBreakdown()
    {
        _factory.AddVariantStock(ProductDetailParentFixture.Variant1Id, 4m);
        _factory.AddVariantStock(ProductDetailParentFixture.Variant2Id, 6m);

        var client = AuthClient();
        var html = await (await client.GetAsync($"/Pantry/Products/Detail/{ProductDetailParentFixture.ParentId}"))
            .Content.ReadAsStringAsync();

        Assert.Contains("10", OnHandTile(html), StringComparison.Ordinal); // aggregate on-hand across both variants
        Assert.Contains(ProductDetailParentFixture.Variant1Name, html, StringComparison.Ordinal);
        Assert.Contains(ProductDetailParentFixture.Variant2Name, html, StringComparison.Ordinal);
        Assert.Contains("Variants", html, StringComparison.Ordinal); // the breakdown section heading
    }

    [Fact(DisplayName = "Detail GET — a variant whose unit can't convert is listed but excluded from the total, with a note")]
    public async Task Get_Parent_UnconvertedVariant_ShowsNoteAndExcludesFromTotal()
    {
        _factory.AddVariantStock(ProductDetailParentFixture.Variant1Id, 4m);
        // Variant 2 stocked in an unrelated unit the identity/factor converter has no bridge for.
        _factory.AddVariantStockInUnit(ProductDetailParentFixture.Variant2Id, 2m, ProductDetailParentFixture.UnrelatedUnitId);
        _factory.UseFactorConverter(); // fails closed for any pair it wasn't told about

        var client = AuthClient();
        var html = await (await client.GetAsync($"/Pantry/Products/Detail/{ProductDetailParentFixture.ParentId}"))
            .Content.ReadAsStringAsync();

        Assert.Contains("other units", html, StringComparison.Ordinal);
        // Aggregate only reflects the convertible variant (4), not the excluded one — pinned by asserting
        // the tile does NOT show 6 (4 + the excluded variant's 2), which is what would render if the
        // excluded variant's on-hand were silently folded in anyway.
        var tile = OnHandTile(html);
        Assert.Contains("4", tile, StringComparison.Ordinal);
        Assert.DoesNotContain("6", tile, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Threshold sheet — enabled on a parent, and re-renders the aggregate as running low")]
    public async Task Threshold_Parent_SetsRule_AndRerendersLowState()
    {
        _factory.AddVariantStock(ProductDetailParentFixture.Variant1Id, 3m);
        var client = AuthClient();
        var token = await GetAntiforgeryTokenAsync(client, ProductDetailParentFixture.ParentId);

        // Threshold sheet copy is parent-specific.
        var sheetHtml = await (await client.GetAsync(
                $"/Pantry/Products/Detail/{ProductDetailParentFixture.ParentId}?handler=ThresholdSheet"))
            .Content.ReadAsStringAsync();
        Assert.Contains("across all variants", sheetHtml, StringComparison.Ordinal);

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["ThresholdInput.Threshold"] = "5",
        });
        var response = await client.PostAsync(
            $"/Pantry/Products/Detail/{ProductDetailParentFixture.ParentId}?handler=SetThreshold", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Running low", html, StringComparison.Ordinal); // 3 <= 5
    }

    [Fact(DisplayName = "Stats panel — sparkline renders from the union of live variants' price history")]
    public async Task Get_Parent_StatsPanel_ShowsSparklineFromUnionHistory()
    {
        _factory.AddVariantStock(ProductDetailParentFixture.Variant1Id, 4m);
        _factory.AddVariantStock(ProductDetailParentFixture.Variant2Id, 6m);
        var client = AuthClient(); // resolves DI, which materializes the parent/variant Product fixtures
        var userId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
        // Keyed by the variants' DOMAIN ids — what PriceHistoryRollup/EffectivePriceRollup actually read
        // (v.Id.Value off IProductRepository), a different id space from the route ids above (mirrors
        // ProductDetailSetPriceTests' own Get_Parent_ShowsCostingRollupPrice pattern).
        _factory.PriceRepo.Items.Add(PriceObservation.Record(
            ProductDetailParentFixture.Household, _factory.Variant1!.Id.Value, null,
            price: 2.00m, quantity: 1m, unitId: ProductDetailParentFixture.UnitId,
            unitPrice: 2.00m, source: PriceSource.Manual, merchantText: null, sourceRef: null,
            observedAt: DateTimeOffset.UtcNow.AddDays(-2), userId: userId));
        _factory.PriceRepo.Items.Add(PriceObservation.Record(
            ProductDetailParentFixture.Household, _factory.Variant2!.Id.Value, null,
            price: 3.00m, quantity: 1m, unitId: ProductDetailParentFixture.UnitId,
            unitPrice: 3.00m, source: PriceSource.Manual, merchantText: null, sourceRef: null,
            observedAt: DateTimeOffset.UtcNow, userId: userId));

        var html = await (await client.GetAsync($"/Pantry/Products/Detail/{ProductDetailParentFixture.ParentId}"))
            .Content.ReadAsStringAsync();

        Assert.Contains("<polyline", html, StringComparison.Ordinal);
        Assert.Contains("median", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Header — a parent shows the variant picker instead of Consume/Add stock")]
    public async Task Get_Parent_PrimaryAction_ShowsVariantPicker()
    {
        _factory.AddVariantStock(ProductDetailParentFixture.Variant1Id, 4m);

        var client = AuthClient();
        var html = await (await client.GetAsync($"/Pantry/Products/Detail/{ProductDetailParentFixture.ParentId}"))
            .Content.ReadAsStringAsync();

        Assert.Contains("Add stock to a variant", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"stock-primary-action\"", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Vitals — Set price stays disabled on a parent's own page")]
    public async Task Get_Parent_SetPriceDisabled()
    {
        _factory.AddVariantStock(ProductDetailParentFixture.Variant1Id, 4m);

        var client = AuthClient();
        var html = await (await client.GetAsync($"/Pantry/Products/Detail/{ProductDetailParentFixture.ParentId}"))
            .Content.ReadAsStringAsync();

        Assert.Contains(Plantry.Web.Pages.Pantry.Products.DetailModel.ParentPriceHint, html, StringComparison.Ordinal);
    }
}

// ── Fixture data ──────────────────────────────────────────────────────────────

internal static class ProductDetailParentFixture
{
    internal static readonly Guid HouseholdId = Guid.Parse("dddddddd-0000-0000-0000-000000000002");
    internal static readonly HouseholdId Household = Plantry.SharedKernel.HouseholdId.From(HouseholdId);
    internal static readonly IClock Clock = Plantry.SharedKernel.Domain.SystemClock.Instance;

    internal static readonly Guid ParentId = Guid.Parse("eeeeeeee-1111-0000-0000-eee000000001");
    internal static readonly Guid Variant1Id = Guid.Parse("eeeeeeee-1111-0000-0000-eee000000002");
    internal static readonly Guid Variant2Id = Guid.Parse("eeeeeeee-1111-0000-0000-eee000000003");
    internal static readonly Guid UnrelatedUnitId = Guid.Parse("ffffffff-1111-0000-0000-fff000000002");

    internal const string Variant1Name = "Bubly Lime";
    internal const string Variant2Name = "Bubly Grapefruit";

    /// <summary>Built once and reused everywhere a unit id is needed (the catalog fake's
    /// <c>DefaultUnitId</c>, the domain <c>Product</c> fixtures' default unit, <see cref="IUnitRepository"/>'s
    /// fake, and every seeded lot/observation) — <see cref="CatalogUnit.Create"/> mints its own id with
    /// no override hook, so a second call would silently produce a DIFFERENT id and break every one of
    /// those call sites' agreement (the median-price unit lookup in particular resolves strictly by id).</summary>
    internal static readonly CatalogUnit Unit = CatalogUnit.Create(Household, "g", "Grams", Dimension.Mass, 1m, isBase: true);
    internal static Guid UnitId => Unit.Id.Value;
}

/// <summary>Multi-product <see cref="ICatalogReadFacade"/> — the one seam <see cref="ProductDetailSetPriceTests"/>'s
/// single-product <c>FakeCatalogReadFacade</c> doesn't cover: <see cref="IOnHandRollupQuery"/> groups
/// live variants by <see cref="CatalogProductInfo.ParentProductId"/> off a single
/// <see cref="ListProductsAsync"/> batch load, so the fake needs to carry the whole household's products,
/// not just the one the route id names.</summary>
internal sealed class FakeParentCatalogReadFacade : ICatalogReadFacade
{
    private readonly List<CatalogProductInfo> _products = [];

    internal void Add(CatalogProductInfo info) => _products.Add(info);

    public Task<CatalogProductInfo?> FindProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(_products.SingleOrDefault(p => p.Id == productId));

    public Task<IReadOnlyList<CatalogProductInfo>> ListProductsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogProductInfo>>(_products);

    public Task<IReadOnlyDictionary<Guid, string>> GetUnitCodesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            _products.GroupBy(p => p.DefaultUnitId).ToDictionary(g => g.Key, g => g.First().DefaultUnitCode));

    public Task<IReadOnlyDictionary<Guid, string>> GetLocationNamesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
}

// ── WAF factory ───────────────────────────────────────────────────────────────

internal sealed class ProductDetailParentFactory : WebApplicationFactory<Program>
{
    internal FakePriceObservationRepository PriceRepo { get; } = new();
    internal FakeDetailStockRepository StockRepo { get; } = new();
    internal FakeParentCatalogReadFacade Catalog { get; } = new();
    internal FakeParentProductRepository ProductRepo { get; } = new();

    /// <summary>The domain <see cref="Product"/> objects the price/stats rollups (via
    /// <see cref="IProductRepository"/>) actually key observations by — a different id space from the
    /// route ids in <see cref="ProductDetailParentFixture"/> (mirrors <c>ProductDetailSetPriceTests</c>'s
    /// own <c>Variant1</c>/<c>Variant2</c> exposure, same reason: <c>Product.Create</c> mints its own
    /// aggregate id).</summary>
    internal Product? Variant1 { get; private set; }
    internal Product? Variant2 { get; private set; }

    /// <summary>Swaps in <see cref="FactorQuantityConverter2"/> (fails closed for any pair not told
    /// about) in place of the default identity converter — exercises the unconvertible-variant path.</summary>
    private bool _useFactorConverter;
    internal void UseFactorConverter() => _useFactorConverter = true;

    internal void AddVariantStock(Guid variantId, decimal quantity) =>
        AddVariantStockInUnit(variantId, quantity, ProductDetailParentFixture.UnitId);

    internal void AddVariantStockInUnit(Guid variantId, decimal quantity, Guid unitId)
    {
        var stock = ProductStock.Start(ProductDetailParentFixture.Household, variantId, ProductDetailParentFixture.Clock);
        stock.AddStock(quantity, unitId, locationId: Guid.NewGuid(), userId: Guid.NewGuid(), ProductDetailParentFixture.Clock);
        StockRepo.Items.Add(stock);
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

            var unit = ProductDetailParentFixture.Unit;
            var f = ProductDetailParentFixture.Household;

            Catalog.Add(new CatalogProductInfo(
                ProductDetailParentFixture.ParentId, "Bubly", "Drinks",
                ProductDetailParentFixture.UnitId, "g", CanHoldStock: false));
            Catalog.Add(new CatalogProductInfo(
                ProductDetailParentFixture.Variant1Id, ProductDetailParentFixture.Variant1Name, "Drinks",
                ProductDetailParentFixture.UnitId, "g", CanHoldStock: true, ParentProductId: ProductDetailParentFixture.ParentId));
            Catalog.Add(new CatalogProductInfo(
                ProductDetailParentFixture.Variant2Id, ProductDetailParentFixture.Variant2Name, "Drinks",
                ProductDetailParentFixture.UnitId, "g", CanHoldStock: true, ParentProductId: ProductDetailParentFixture.ParentId));

            services.RemoveAll<IUnitRepository>();
            services.AddSingleton<IUnitRepository>(new FakeSingleUnitRepository(unit));

            services.RemoveAll<ICatalogReadFacade>();
            services.AddSingleton<ICatalogReadFacade>(Catalog);

            services.RemoveAll<IProductStockRepository>();
            services.AddSingleton<IProductStockRepository>(StockRepo);

            services.RemoveAll<ILowStockRuleRepository>();
            services.AddSingleton<ILowStockRuleRepository>(new FakeLowStockRuleRepository());

            services.RemoveAll<IProductConversionProvider>();
            services.AddSingleton<IProductConversionProvider>(
                _useFactorConverter
                    ? new IdentityFailClosedConversionProvider()
                    : (IProductConversionProvider)new IdentityConversionProvider());

            services.RemoveAll<IStockProvenanceReader>();
            services.AddSingleton<IStockProvenanceReader>(new FakeStockProvenanceReader());

            services.RemoveAll<IPriceObservationRepository>();
            services.AddSingleton<IPriceObservationRepository>(PriceRepo);

            services.RemoveAll<IDisplayCurrency>();
            services.AddSingleton<IDisplayCurrency>(new FakeDisplayCurrency());

            services.RemoveAll<IUnitPriceCalculator>();
            services.AddSingleton<IUnitPriceCalculator>(new FakeUnitPriceCalculator(0.5m));

            // The parent price/stats rollups (BuildPriceDisplayAsync / BuildStatsAsync) read variants
            // through IProductRepository, not the catalog facade (plantry-i07l precedent) — populate the
            // same parent/variant ids there too so both rollups agree on which variants are live.
            var parent = Product.Create(f, "Bubly", UnitId.From(ProductDetailParentFixture.UnitId), ProductDetailParentFixture.Clock);
            parent.SetHasVariants(true, ProductDetailParentFixture.Clock);
            var v1 = Product.Create(f, ProductDetailParentFixture.Variant1Name, UnitId.From(ProductDetailParentFixture.UnitId), ProductDetailParentFixture.Clock);
            v1.MakeVariantOf(parent.Id, ProductDetailParentFixture.Clock);
            var v2 = Product.Create(f, ProductDetailParentFixture.Variant2Name, UnitId.From(ProductDetailParentFixture.UnitId), ProductDetailParentFixture.Clock);
            v2.MakeVariantOf(parent.Id, ProductDetailParentFixture.Clock);
            ProductRepo.LoadParent(
                (ProductDetailParentFixture.ParentId, parent),
                (ProductDetailParentFixture.Variant1Id, v1),
                (ProductDetailParentFixture.Variant2Id, v2));
            (Variant1, Variant2) = (v1, v2);
            services.RemoveAll<IProductRepository>();
            services.AddSingleton<IProductRepository>(ProductRepo);

            services.RemoveAll<Plantry.Recipes.Domain.IRecipeRepository>();
            services.AddSingleton<Plantry.Recipes.Domain.IRecipeRepository>(new FakeRecipeRepository());
        });
    }
}

/// <summary>Identity for a matching unit; fails for any pair it wasn't told about — used only by the
/// unconverted-variant test (plantry-oh27.7) to exercise <see cref="OnHandLevel.UnconvertedVariantIds"/>.</summary>
internal sealed class IdentityFailClosedConversionProvider : IProductConversionProvider
{
    public Task<IQuantityConverter> ForProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult<IQuantityConverter>(new FailClosedConverter());

    private sealed class FailClosedConverter : IQuantityConverter
    {
        public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId) =>
            fromUnitId == toUnitId ? amount : Error.Custom("Test.Unresolvable", "no conversion");
    }
}
