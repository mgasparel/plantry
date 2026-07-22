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
/// L4 Web integration tests for the "Set price" sheet on the Pantry product Detail page (plantry-3fqm) —
/// mirrors <c>ProductDetailAddVariantTests</c>' shape (Catalog's own Detail page). The Inventory/Catalog
/// seams are replaced by in-memory fakes so no database is touched; <c>PricingQueries</c> and
/// <c>DisplayCurrencyAccessor</c> are left as the real concrete types, resolving against a faked
/// <see cref="IPriceObservationRepository"/> and a faked <see cref="IDisplayCurrency"/> (the real
/// service hits a live DB even on its "no household row" USD-fallback path).
///
/// Also regression-covers a cross-form <c>[BindProperty]</c> validation bug this feature would
/// otherwise have introduced: <see cref="Plantry.Web.Pages.Pantry.Products.DetailModel"/> carries three
/// sibling bound forms (Consume, Set alert, Set price), and Razor Pages validates every
/// <c>[BindProperty]</c> on every POST regardless of which handler ran — so without
/// <c>DetailModel.ClearOtherSheetValidation</c>, posting to any one sheet would fail
/// <c>ModelState.IsValid</c> on the other two sheets' unrelated <c>[Required]</c> fields.
/// </summary>
public sealed class ProductDetailSetPriceTests : IDisposable
{
    private readonly ProductDetailSetPriceFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static readonly Guid HouseholdId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");

    private HttpClient AuthClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdId.ToString());
        return client;
    }

    /// <summary>Harvests the antiforgery token from the Detail page GET so the POST round-trips work.</summary>
    private async Task<string> GetAntiforgeryTokenAsync(HttpClient client, Guid productId)
    {
        var html = await (await client.GetAsync($"/Pantry/Products/Detail/{productId}"))
            .Content.ReadAsStringAsync();

        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the Detail page.");
        return match.Groups[1].Value;
    }

    [Fact(DisplayName = "SetPriceSheet — renders the sheet")]
    public async Task SetPriceSheet_Renders()
    {
        var client = AuthClient();

        var response = await client.GetAsync(
            $"/Pantry/Products/Detail/{ProductDetailSetPriceFixture.ProductId}?handler=SetPriceSheet");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Set price", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "SetPrice — happy path records a Manual observation with no store/merchant and no source ref")]
    public async Task SetPrice_HappyPath_RecordsManualObservation()
    {
        var client = AuthClient();
        var productId = ProductDetailSetPriceFixture.ProductId;
        var token = await GetAntiforgeryTokenAsync(client, productId);

        var response = await client.PostAsync(
            $"/Pantry/Products/Detail/{productId}?handler=SetPrice",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("PriceInput.Price", "3.99"),
                new KeyValuePair<string, string>("PriceInput.Quantity", "500"),
                new KeyValuePair<string, string>("PriceInput.UnitId", ProductDetailSetPriceFixture.UnitId.ToString()),
            }));

        // The response is a single OOB fragment (mirrors _StockDetail's OOB idiom for Consume/Threshold) —
        // its content is swapped elsewhere, so the sheet-host target ends up empty, closing the sheet.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("hx-swap-oob", html, StringComparison.Ordinal);
        Assert.Contains("3.99", html, StringComparison.Ordinal);

        var saved = Assert.Single(_factory.PriceRepo.Items);
        Assert.Equal(PriceSource.Manual, saved.Source);
        Assert.Equal(3.99m, saved.Price);
        Assert.Equal(500m, saved.Quantity);
        Assert.Equal(ProductDetailSetPriceFixture.UnitId, saved.UnitId);
        Assert.Null(saved.SourceRef);
        Assert.Null(saved.MerchantText);
        Assert.Null(saved.StoreId);
        Assert.Equal(1, _factory.PriceRepo.SaveChangesCalls);
    }

    [Fact(DisplayName = "SetPrice — missing price returns a model error and records nothing")]
    public async Task SetPrice_MissingPrice_ReturnsModelError()
    {
        var client = AuthClient();
        var productId = ProductDetailSetPriceFixture.ProductId;
        var token = await GetAntiforgeryTokenAsync(client, productId);

        var response = await client.PostAsync(
            $"/Pantry/Products/Detail/{productId}?handler=SetPrice",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("PriceInput.Quantity", "1"),
                new KeyValuePair<string, string>("PriceInput.UnitId", ProductDetailSetPriceFixture.UnitId.ToString()),
            }));

        // Model error re-renders the sheet partial (not a redirect) — same OK-not-Redirect signal
        // ProductDetailAddVariantTests uses for its stock-block assertion.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_factory.PriceRepo.Items);
        Assert.Equal(0, _factory.PriceRepo.SaveChangesCalls);
    }

    [Fact(DisplayName = "SetThreshold — still succeeds now the page also carries PriceInput's required fields (cross-form validation regression)")]
    public async Task SetThreshold_StillSucceeds_WithPriceInputPresentOnPage()
    {
        var client = AuthClient();
        var productId = ProductDetailSetPriceFixture.ProductId;
        var token = await GetAntiforgeryTokenAsync(client, productId);

        // Posts only ThresholdInput.Threshold — PriceInput.Price/Quantity/UnitId are all [Required] and
        // absent here, exactly the shape that broke ModelState.IsValid before ClearOtherSheetValidation.
        var response = await client.PostAsync(
            $"/Pantry/Products/Detail/{productId}?handler=SetThreshold",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("ThresholdInput.Threshold", "2"),
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("hx-swap-oob", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Detail GET — shows a placeholder when no price has ever been observed")]
    public async Task Get_ShowsPlaceholder_WhenNoObservationExists()
    {
        var client = AuthClient();

        var response = await client.GetAsync($"/Pantry/Products/Detail/{ProductDetailSetPriceFixture.ProductId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("No price recorded yet", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Detail GET — shows the current effective price (same read model CostingService uses) once one is recorded")]
    public async Task Get_ShowsEffectivePrice_OnceRecorded()
    {
        _factory.PriceRepo.Items.Add(PriceObservation.Record(
            ProductDetailSetPriceFixture.Household, ProductDetailSetPriceFixture.ProductId, null,
            price: 2.99m, quantity: 1m, unitId: ProductDetailSetPriceFixture.UnitId,
            unitPrice: 2.99m, source: PriceSource.Manual, merchantText: null, sourceRef: null,
            observedAt: DateTimeOffset.UtcNow, userId: Guid.Parse("00000000-0000-0000-0000-0000000000aa")));

        var client = AuthClient();
        var response = await client.GetAsync($"/Pantry/Products/Detail/{ProductDetailSetPriceFixture.ProductId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("2.99", html, StringComparison.Ordinal);
    }
}

// ── Fixture data ──────────────────────────────────────────────────────────────

internal static class ProductDetailSetPriceFixture
{
    internal static readonly Guid HouseholdId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    internal static readonly HouseholdId Household = Plantry.SharedKernel.HouseholdId.From(HouseholdId);
    internal static readonly IClock Clock = Plantry.SharedKernel.Domain.SystemClock.Instance;

    internal static readonly Guid ProductId = Guid.Parse("eeeeeeee-0000-0000-0000-eee000000001");
    internal static readonly Guid UnitId = Guid.Parse("ffffffff-0000-0000-0000-fff000000001");

    internal static CatalogUnit BuildUnit() =>
        CatalogUnit.Create(Household, "g", "Grams", Dimension.Mass, 1m, isBase: true);

    /// <summary>Stock with no active lots — mirrors the "seeded pantry stock, no price" scenario
    /// plantry-3fqm exists to fix: quantities came in via Take Stock, cost never did.</summary>
    internal static ProductStock BuildStock() => ProductStock.Start(Household, ProductId, Clock);
}

// ── WAF factory ───────────────────────────────────────────────────────────────

internal sealed class ProductDetailSetPriceFactory : WebApplicationFactory<Program>
{
    internal FakePriceObservationRepository PriceRepo { get; } = new();

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

            var unit = ProductDetailSetPriceFixture.BuildUnit();
            var stock = ProductDetailSetPriceFixture.BuildStock();

            services.RemoveAll<IUnitRepository>();
            services.AddSingleton<IUnitRepository>(new FakeSingleUnitRepository(unit));

            services.RemoveAll<ICatalogReadFacade>();
            services.AddSingleton<ICatalogReadFacade>(new FakeCatalogReadFacade(ProductDetailSetPriceFixture.ProductId, unit));

            var stockRepo = new FakeDetailStockRepository();
            stockRepo.Items.Add(stock);
            services.RemoveAll<IProductStockRepository>();
            services.AddSingleton<IProductStockRepository>(stockRepo);

            services.RemoveAll<IProductConversionProvider>();
            services.AddSingleton<IProductConversionProvider>(new IdentityConversionProvider());

            services.RemoveAll<IStockProvenanceReader>();
            services.AddSingleton<IStockProvenanceReader>(new FakeStockProvenanceReader());

            // Pricing seam: a fake repository so no database is touched. PricingQueries stays the real
            // concrete type — it resolves fine over this fake.
            services.RemoveAll<IPriceObservationRepository>();
            services.AddSingleton<IPriceObservationRepository>(PriceRepo);

            // DisplayCurrencyAccessor stays the real concrete type but its IDisplayCurrency source is
            // faked — the real DisplayCurrencyService queries Household over a live DB connection even
            // when it ultimately falls back to "USD", so it cannot run DB-less.
            services.RemoveAll<IDisplayCurrency>();
            services.AddSingleton<IDisplayCurrency>(new FakeDisplayCurrency());

            services.RemoveAll<IUnitPriceCalculator>();
            services.AddSingleton<IUnitPriceCalculator>(new FakeUnitPriceCalculator(0.5m));

            // plantry-o0r8: Detail's GET path now also resolves the "Recipes" section
            // (RecipesUsingProductQuery), which reads through IRecipeRepository — otherwise the real
            // EF-backed repository, needing a live Postgres connection. Reuses the empty fake from
            // ProductDetailRecipesSectionTests.cs (same namespace); this test doesn't care about the
            // Recipes section's content.
            services.RemoveAll<Plantry.Recipes.Domain.IRecipeRepository>();
            services.AddSingleton<Plantry.Recipes.Domain.IRecipeRepository>(new FakeRecipeRepository());
        });
    }
}

// ── Fake implementations ──────────────────────────────────────────────────────

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

internal sealed class FakeCatalogReadFacade(Guid productId, CatalogUnit unit) : ICatalogReadFacade
{
    public Task<CatalogProductInfo?> FindProductAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult<CatalogProductInfo?>(id == productId
            ? new CatalogProductInfo(productId, "Test Product", "Pantry", unit.Id.Value, unit.Code, CanHoldStock: true)
            : null);

    public Task<IReadOnlyList<CatalogProductInfo>> ListProductsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogProductInfo>>(
            [new CatalogProductInfo(productId, "Test Product", "Pantry", unit.Id.Value, unit.Code, CanHoldStock: true)]);

    public Task<IReadOnlyDictionary<Guid, string>> GetUnitCodesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string> { [unit.Id.Value] = unit.Code });

    public Task<IReadOnlyDictionary<Guid, string>> GetLocationNamesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
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

internal sealed class FakeStockProvenanceReader : IStockProvenanceReader
{
    public Task<IReadOnlyDictionary<Guid, ProvenanceChip>> ResolveAsync(
        IReadOnlyList<(Guid JournalId, StockSourceType SourceType, Guid? SourceRef)> rows, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, ProvenanceChip>>(new Dictionary<Guid, ProvenanceChip>());
}

/// <summary>No lots are ever seeded in these tests, so no conversion is actually exercised — identity
/// is a safe stand-in.</summary>
internal sealed class IdentityConversionProvider : IProductConversionProvider
{
    public Task<IQuantityConverter> ForProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult<IQuantityConverter>(new IdentityConverter());

    private sealed class IdentityConverter : IQuantityConverter
    {
        public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId) => amount;
    }
}

internal sealed class FakeUnitPriceCalculator(decimal? returnValue) : IUnitPriceCalculator
{
    public Task<decimal?> TryNormalizeAsync(decimal price, decimal quantity, Guid unitId, CancellationToken ct = default) =>
        Task.FromResult(returnValue);
}

internal sealed class FakeDisplayCurrency : IDisplayCurrency
{
    public Task<string> GetAsync(CancellationToken ct = default) => Task.FromResult("USD");
}

internal sealed class FakePriceObservationRepository : IPriceObservationRepository
{
    internal List<PriceObservation> Items { get; } = [];
    internal int SaveChangesCalls { get; private set; }

    public Task AddAsync(PriceObservation observation, CancellationToken ct = default)
    {
        Items.Add(observation);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCalls++;
        return Task.CompletedTask;
    }

    public Task<PriceObservation?> FindAsync(PriceObservationId id, CancellationToken ct = default) =>
        Task.FromResult(Items.FirstOrDefault(p => p.Id == id));

    public Task<IReadOnlyList<PriceObservation>> ListPurchasesAwaitingStoreAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PriceObservation>>(Items
            .Where(p => p.Source == PriceSource.Purchase && p.StoreId is null && !string.IsNullOrWhiteSpace(p.MerchantText))
            .OrderBy(p => p.ObservedAt)
            .ToList());

    public Task<PriceObservation?> LatestForProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(Items
            .Where(p => p.ProductId == productId && (p.Source == PriceSource.Purchase || p.Source == PriceSource.Manual))
            .MaxBy(p => p.ObservedAt));

    public Task<PriceObservation?> LatestForSkuAsync(Guid skuId, CancellationToken ct = default) =>
        Task.FromResult(Items
            .Where(p => p.SkuId == skuId && (p.Source == PriceSource.Purchase || p.Source == PriceSource.Manual))
            .MaxBy(p => p.ObservedAt));

    public Task<PriceObservation?> CheapestActiveDealForProductAsync(Guid productId, DateOnly today, CancellationToken ct = default) =>
        Task.FromResult(Items
            .Where(p => p.ProductId == productId && p.Source == PriceSource.Deal
                && p.ValidFrom <= today && p.ValidTo >= today)
            .OrderBy(p => p.UnitPrice)
            .ThenBy(p => p.Price)
            .FirstOrDefault());
}
