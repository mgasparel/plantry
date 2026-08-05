using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Pantry.Application;
using Plantry.Pantry.Domain;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;
using Plantry.Web.Pages.Catalog.Products;
using CatalogUnit = Plantry.Pantry.Domain.Unit;

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

    [Fact(DisplayName = "Edit — root product renders Default for null local freeze/thaw Never decisions")]
    public async Task Edit_RootProduct_RendersDefaultPolicyModes()
    {
        var client = AuthClient();
        var html = await (await client.GetAsync($"/Catalog/Products/{_factory.TrackedStandaloneId}"))
            .Content.ReadAsStringAsync();

        Assert.Matches(
            "name=\"Input\\.AfterFreezingMode\" value=\"Default\"[^>]*checked",
            html);
        Assert.Matches(
            "name=\"Input\\.AfterThawingMode\" value=\"Default\"[^>]*checked",
            html);
        Assert.DoesNotContain("value=\"Inherit\"", html);
        Assert.Equal(2, Regex.Matches(html, "x-bind:disabled=\"mode !== 'SetDays'\"").Count);
    }

    [Fact(DisplayName = "Edit — root SetDays persists both local day overrides and enables the effective-day mode")]
    public async Task Edit_RootProduct_SetDays_PersistsDayOverrides()
    {
        var client = AuthClient();
        var productId = _factory.TrackedStandaloneId;
        var product = _factory.ProductRepo.Items.Single(p => p.Id.Value == productId);
        var token = await GetAntiforgeryTokenAsync(client, productId);

        var response = await client.PostAsync($"/Catalog/Products/{productId}",
            PolicyForm(token, product, ProductExpiryMode.SetDays, ProductExpiryMode.SetDays, 14, 6));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(product.NeverExpiresAfterFreezing);
        Assert.False(product.NeverExpiresAfterThawing);
        Assert.Equal(14, product.DefaultDueDaysAfterFreezing);
        Assert.Equal(6, product.DefaultDueDaysAfterThawing);

        var html = await (await client.GetAsync($"/Catalog/Products/{productId}")).Content.ReadAsStringAsync();
        Assert.Equal(2, Regex.Matches(
            html, "name=\"Input\\.After(?:Freezing|Thawing)Mode\" value=\"SetDays\"[^>]*checked").Count);
        Assert.Equal(2, Regex.Matches(html, "x-bind:disabled=\"mode !== 'SetDays'\"").Count);
    }

    [Fact(DisplayName = "Edit — variant renders live Inherit even when snapshot day defaults exist")]
    public async Task Edit_VariantProduct_RendersLiveInheritModes()
    {
        var client = AuthClient();
        var parent = _factory.ProductRepo.Items.Single(p => p.Id.Value == _factory.ParentId);
        parent.SetNeverExpiryOverrides(true, false, ProductDetailTrackStockFactory.Clock);
        var variant = _factory.ProductRepo.Items.Single(p => p.Id.Value == _factory.VariantId);
        variant.SetExpiryDefaults(null, null, 90, 3, ProductDetailTrackStockFactory.Clock);

        var html = await (await client.GetAsync($"/Catalog/Products/{_factory.VariantId}"))
            .Content.ReadAsStringAsync();

        Assert.Equal(2, Regex.Matches(
            html, "name=\"Input\\.After(?:Freezing|Thawing)Mode\" value=\"Inherit\"[^>]*checked").Count);
        Assert.Contains("Follows the parent", html);
        Assert.Contains("Never rule live", html);
    }

    [Fact(DisplayName = "Edit — variant posts Inherit, Default, SetDays, and Never with the documented persistence semantics")]
    public async Task Edit_VariantProduct_PostsEveryPolicyMode()
    {
        var client = AuthClient();
        var productId = _factory.VariantId;
        var product = _factory.ProductRepo.Items.Single(p => p.Id.Value == productId);
        product.SetExpiryDefaults(null, null, 21, 8, ProductDetailTrackStockFactory.Clock);

        var token = await GetAntiforgeryTokenAsync(client, productId);
        var inherit = await client.PostAsync($"/Catalog/Products/{productId}",
            PolicyForm(token, product, ProductExpiryMode.Inherit, ProductExpiryMode.Inherit));
        Assert.Equal(HttpStatusCode.Redirect, inherit.StatusCode);
        Assert.Null(product.NeverExpiresAfterFreezing);
        Assert.Null(product.NeverExpiresAfterThawing);
        Assert.Equal(21, product.DefaultDueDaysAfterFreezing);
        Assert.Equal(8, product.DefaultDueDaysAfterThawing);
        AssertPolicyMode(await GetDetailHtmlAsync(client, productId), ProductExpiryMode.Inherit);

        token = await GetAntiforgeryTokenAsync(client, productId);
        var @default = await client.PostAsync($"/Catalog/Products/{productId}",
            PolicyForm(token, product, ProductExpiryMode.Default, ProductExpiryMode.Default));
        Assert.Equal(HttpStatusCode.Redirect, @default.StatusCode);
        Assert.False(product.NeverExpiresAfterFreezing);
        Assert.False(product.NeverExpiresAfterThawing);
        Assert.Null(product.DefaultDueDaysAfterFreezing);
        Assert.Null(product.DefaultDueDaysAfterThawing);
        AssertPolicyMode(await GetDetailHtmlAsync(client, productId), ProductExpiryMode.Default);

        token = await GetAntiforgeryTokenAsync(client, productId);
        var setDays = await client.PostAsync($"/Catalog/Products/{productId}",
            PolicyForm(token, product, ProductExpiryMode.SetDays, ProductExpiryMode.SetDays, 17, 4));
        Assert.Equal(HttpStatusCode.Redirect, setDays.StatusCode);
        Assert.False(product.NeverExpiresAfterFreezing);
        Assert.False(product.NeverExpiresAfterThawing);
        Assert.Equal(17, product.DefaultDueDaysAfterFreezing);
        Assert.Equal(4, product.DefaultDueDaysAfterThawing);
        AssertPolicyMode(await GetDetailHtmlAsync(client, productId), ProductExpiryMode.SetDays);

        token = await GetAntiforgeryTokenAsync(client, productId);
        var never = await client.PostAsync($"/Catalog/Products/{productId}",
            PolicyForm(token, product, ProductExpiryMode.Never, ProductExpiryMode.Never));
        Assert.Equal(HttpStatusCode.Redirect, never.StatusCode);
        Assert.True(product.NeverExpiresAfterFreezing);
        Assert.True(product.NeverExpiresAfterThawing);
        Assert.Null(product.DefaultDueDaysAfterFreezing);
        Assert.Null(product.DefaultDueDaysAfterThawing);
        AssertPolicyMode(await GetDetailHtmlAsync(client, productId), ProductExpiryMode.Never);
    }

    [Fact(DisplayName = "Edit — Never policy clears the stored day value and Default clears the Never decision")]
    public async Task Edit_NeverThenDefault_ClearsEachLocalPolicy()
    {
        var client = AuthClient();
        var productId = _factory.TrackedStandaloneId;
        var product = _factory.ProductRepo.Items.Single(p => p.Id.Value == productId);
        product.SetExpiryDefaults(null, null, 14, 7, ProductDetailTrackStockFactory.Clock);
        var token = await GetAntiforgeryTokenAsync(client, productId);

        var neverResponse = await client.PostAsync($"/Catalog/Products/{productId}",
            PolicyForm(token, product, ProductExpiryMode.Never, ProductExpiryMode.Never));

        Assert.Equal(HttpStatusCode.Redirect, neverResponse.StatusCode);
        Assert.True(product.NeverExpiresAfterFreezing);
        Assert.True(product.NeverExpiresAfterThawing);
        Assert.Null(product.DefaultDueDaysAfterFreezing);
        Assert.Null(product.DefaultDueDaysAfterThawing);

        token = await GetAntiforgeryTokenAsync(client, productId);
        var defaultResponse = await client.PostAsync($"/Catalog/Products/{productId}",
            PolicyForm(token, product, ProductExpiryMode.Default, ProductExpiryMode.Default));

        Assert.Equal(HttpStatusCode.Redirect, defaultResponse.StatusCode);
        Assert.Null(product.NeverExpiresAfterFreezing);
        Assert.Null(product.NeverExpiresAfterThawing);
        Assert.Null(product.DefaultDueDaysAfterFreezing);
        Assert.Null(product.DefaultDueDaysAfterThawing);
    }

    [Theory(DisplayName = "Edit — invalid SetDays values return validation feedback without changing the product")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public async Task Edit_SetDays_InvalidValue_ShowsFieldErrorAndDoesNotPersist(string invalidDays)
    {
        var client = AuthClient();
        var productId = _factory.TrackedStandaloneId;
        var product = _factory.ProductRepo.Items.Single(p => p.Id.Value == productId);
        var token = await GetAntiforgeryTokenAsync(client, productId);

        var response = await client.PostAsync($"/Catalog/Products/{productId}",
            RawPolicyForm(token, product, ProductExpiryMode.SetDays, ProductExpiryMode.Default,
                freezingDays: invalidDays));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("name=\"Input.DefaultDueDaysAfterFreezing\"", html);
        AssertFieldError(html, "Input.DefaultDueDaysAfterFreezing");
        Assert.Null(product.NeverExpiresAfterFreezing);
        Assert.Null(product.DefaultDueDaysAfterFreezing);
    }

    [Fact(DisplayName = "Edit — empty SetDays value displays a field error and leaves the product unchanged")]
    public async Task Edit_SetDays_EmptyValue_ShowsFieldErrorAndDoesNotPersist()
    {
        var client = AuthClient();
        var productId = _factory.TrackedStandaloneId;
        var product = _factory.ProductRepo.Items.Single(p => p.Id.Value == productId);
        var token = await GetAntiforgeryTokenAsync(client, productId);

        var response = await client.PostAsync($"/Catalog/Products/{productId}",
            RawPolicyForm(token, product, ProductExpiryMode.SetDays, ProductExpiryMode.Default,
                freezingDays: string.Empty));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Enter the number of days for a custom expiry policy.", html);
        AssertFieldError(html, "Input.DefaultDueDaysAfterFreezing");
        Assert.Null(product.NeverExpiresAfterFreezing);
        Assert.Null(product.DefaultDueDaysAfterFreezing);
    }

    [Fact(DisplayName = "Edit — undefined expiry mode returns field validation, reloads the current mode, and does not mutate or redirect")]
    public async Task Edit_UndefinedExpiryMode_ShowsFieldErrorAndDoesNotMutateOrRedirect()
    {
        var client = AuthClient();
        var productId = _factory.TrackedStandaloneId;
        var product = _factory.ProductRepo.Items.Single(p => p.Id.Value == productId);
        product.SetNeverExpiryOverrides(true, false, ProductDetailTrackStockFactory.Clock);
        product.SetExpiryDefaults(null, null, null, 7, ProductDetailTrackStockFactory.Clock);
        var beforeName = product.Name;
        var beforeFreezingNever = product.NeverExpiresAfterFreezing;
        var beforeThawingNever = product.NeverExpiresAfterThawing;
        var beforeFreezingDays = product.DefaultDueDaysAfterFreezing;
        var beforeThawingDays = product.DefaultDueDaysAfterThawing;
        var token = await GetAntiforgeryTokenAsync(client, productId);

        var response = await client.PostAsync($"/Catalog/Products/{productId}",
            RawPolicyForm(token, product, freezingMode: "999", thawingMode: "Default",
                freezingDays: "14", thawingDays: "19"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        AssertFieldError(html, "Input.AfterFreezingMode");
        Assert.Contains("Select a valid expiry policy.", html, StringComparison.Ordinal);
        Assert.Matches(
            "name=\"Input\\.AfterFreezingMode\" value=\"Never\"[^>]*checked",
            html);
        Assert.Equal(beforeName, product.Name);
        Assert.Equal(beforeFreezingNever, product.NeverExpiresAfterFreezing);
        Assert.Equal(beforeThawingNever, product.NeverExpiresAfterThawing);
        Assert.Equal(beforeFreezingDays, product.DefaultDueDaysAfterFreezing);
        Assert.Equal(beforeThawingDays, product.DefaultDueDaysAfterThawing);
    }

    private static void AssertFieldError(string html, string fieldName) =>
        Assert.Matches(
            $"<span(?=[^>]*data-valmsg-for=\"{Regex.Escape(fieldName)}\")(?=[^>]*class=\"[^\"]*field__error[^\"]*\")[^>]*>",
            html);

    private async Task<string> GetDetailHtmlAsync(HttpClient client, Guid productId) =>
        await (await client.GetAsync($"/Catalog/Products/{productId}")).Content.ReadAsStringAsync();

    private static void AssertPolicyMode(string html, ProductExpiryMode expectedMode)
    {
        Assert.Equal(2, Regex.Matches(
            html, $"name=\"Input\\.After(?:Freezing|Thawing)Mode\" value=\"{expectedMode}\"[^>]*checked").Count);
        Assert.Equal(2, Regex.Matches(html, "x-bind:disabled=\"mode !== 'SetDays'\"").Count);
    }

    private static FormUrlEncodedContent PolicyForm(
        string token, Product product, ProductExpiryMode freezingMode, ProductExpiryMode thawingMode,
        int? freezingDays = null, int? thawingDays = null) =>
        new(
        [
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            new KeyValuePair<string, string>("Input.Name", product.Name),
            new KeyValuePair<string, string>("Input.DefaultUnitId", product.DefaultUnitId.Value.ToString()),
            new KeyValuePair<string, string>("Input.TrackStock", product.TrackStock ? "true" : "false"),
            new KeyValuePair<string, string>("Input.AfterFreezingMode", freezingMode.ToString()),
            new KeyValuePair<string, string>("Input.AfterThawingMode", thawingMode.ToString()),
            .. freezingDays is { } f
                ? new[] { new KeyValuePair<string, string>("Input.DefaultDueDaysAfterFreezing", f.ToString()) }
                : [],
            .. thawingDays is { } t
                ? new[] { new KeyValuePair<string, string>("Input.DefaultDueDaysAfterThawing", t.ToString()) }
                : [],
        ]);

    private static FormUrlEncodedContent RawPolicyForm(
        string token, Product product, ProductExpiryMode freezingMode, ProductExpiryMode thawingMode,
        string? freezingDays = null, string? thawingDays = null) =>
        RawPolicyForm(token, product, freezingMode.ToString(), thawingMode.ToString(), freezingDays, thawingDays);

    private static FormUrlEncodedContent RawPolicyForm(
        string token, Product product, string freezingMode, string thawingMode,
        string? freezingDays = null, string? thawingDays = null) =>
        new(
        [
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            new KeyValuePair<string, string>("Input.Name", product.Name),
            new KeyValuePair<string, string>("Input.DefaultUnitId", product.DefaultUnitId.Value.ToString()),
            new KeyValuePair<string, string>("Input.TrackStock", product.TrackStock ? "true" : "false"),
            new KeyValuePair<string, string>("Input.AfterFreezingMode", freezingMode.ToString()),
            new KeyValuePair<string, string>("Input.AfterThawingMode", thawingMode.ToString()),
            .. freezingDays is not null
                ? new[] { new KeyValuePair<string, string>("Input.DefaultDueDaysAfterFreezing", freezingDays) }
                : [],
            .. thawingDays is not null
                ? new[] { new KeyValuePair<string, string>("Input.DefaultDueDaysAfterThawing", thawingDays) }
                : [],
        ]);
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
            services.AddFakeHouseholdExpiryDefaults();
            services.AddFakeSubstitutions();
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
    internal static readonly IClock Clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
    internal FakeProductRepo ProductRepo { get; private set; } = new();
    internal Guid TrackedStandaloneId { get; private set; }
    internal Guid UntrackedStandaloneId { get; private set; }
    internal Guid ParentId { get; private set; }
    internal Guid VariantId { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.AddFakeExpiringSoonHorizon();
            services.AddFakeHouseholdExpiryDefaults();
            services.AddFakeSubstitutions();
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            var household = Plantry.SharedKernel.HouseholdId.From(Guid.Parse("cccccccc-0000-0000-0000-000000000001"));
            var clock = Clock;
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
            VariantId = parentVariant.Id.Value;

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
