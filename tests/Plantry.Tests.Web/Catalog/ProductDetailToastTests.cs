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
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Web.Catalog;

/// <summary>
/// L4 Web integration tests for the shared TempData["ToastMessage"] + _Layout.cshtml toast pattern
/// (plantry-u7n9). Each of the 11 mutating handlers on Catalog/Products/Detail sets a specific
/// success message immediately before its existing RedirectToPage(...) call; these tests round-trip
/// each handler's happy path through the WAF (POST, then follow the redirect with the same client so
/// TempData's cookie survives) and assert the exact toast markup + message text renders on the next
/// GET.
///
/// The three existing bespoke toast implementations (Deals/Review's htmx HX-Trigger + Alpine host,
/// Take Stock's and Intake Review's Preact-island signal state) are untouched by this change — see
/// <c>DealReviewPageTests</c> for the existing regression coverage on that mechanism, unmodified here.
/// </summary>
public sealed class ProductDetailToastTests : IDisposable
{
    private readonly ToastFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient AuthClient(bool allowAutoRedirect = false)
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = allowAutoRedirect });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ToastFixture.HouseholdId.ToString());
        return client;
    }

    private async Task<string> GetAntiforgeryTokenAsync(HttpClient client, Guid productId)
    {
        var html = await (await client.GetAsync($"/Catalog/Products/{productId}")).Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the Detail page.");
        return match.Groups[1].Value;
    }

    /// <summary>
    /// Posts a handler, follows the redirect with the same client (so the TempData cookie set by
    /// the POST response is sent back on the next GET), and returns the rendered HTML of the page
    /// the redirect lands on.
    /// </summary>
    private static async Task<string> PostAndFollowAsync(
        HttpClient client, string path, IEnumerable<KeyValuePair<string, string>> form)
    {
        var response = await client.PostAsync(path, new FormUrlEncodedContent(form));
        if (response.StatusCode != HttpStatusCode.Redirect)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new Exception($"Expected redirect, got {response.StatusCode}. Body:\n{body}");
        }
        var location = response.Headers.Location!.ToString();
        var redirected = await client.GetAsync(location);
        return await redirected.Content.ReadAsStringAsync();
    }

    private static void AssertToast(string html, string message)
    {
        Assert.Contains("class=\"toast\"", html);
        Assert.Contains(message, html);
    }

    [Fact(DisplayName = "OnPostAsync (edit) — toasts 'Product updated.'")]
    public async Task Edit_Toasts_ProductUpdated()
    {
        var client = AuthClient();
        var id = ToastFixture.EditProductId;
        var token = await GetAntiforgeryTokenAsync(client, id);

        var html = await PostAndFollowAsync(client, $"/Catalog/Products/{id}", new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            new KeyValuePair<string, string>("Input.Name", "Updated Name"),
            new KeyValuePair<string, string>("Input.DefaultUnitId", _factory.EachUnitId.ToString()),
            new KeyValuePair<string, string>("Input.TrackStock", "true"),
        });

        AssertToast(html, "Product updated.");
    }

    [Fact(DisplayName = "AddSku — toasts 'SKU added.'")]
    public async Task AddSku_Toasts_SkuAdded()
    {
        var client = AuthClient();
        var id = ToastFixture.SkuHostId;
        var token = await GetAntiforgeryTokenAsync(client, id);

        var html = await PostAndFollowAsync(client, $"/Catalog/Products/{id}?handler=AddSku", new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            new KeyValuePair<string, string>("SkuInput.Label", "New SKU"),
        });

        AssertToast(html, "SKU added.");
    }

    [Fact(DisplayName = "RemoveSku — toasts 'SKU removed.'")]
    public async Task RemoveSku_Toasts_SkuRemoved()
    {
        var client = AuthClient();
        var id = ToastFixture.SkuHostId;
        var token = await GetAntiforgeryTokenAsync(client, id);

        var html = await PostAndFollowAsync(
            client, $"/Catalog/Products/{id}?handler=RemoveSku&skuId={_factory.ExistingSkuId!.Value.Value}", new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
            });

        AssertToast(html, "SKU removed.");
    }

    [Fact(DisplayName = "AddConversion — toasts 'Conversion added.'")]
    public async Task AddConversion_Toasts_ConversionAdded()
    {
        var client = AuthClient();
        var id = ToastFixture.ConversionHostId;
        var token = await GetAntiforgeryTokenAsync(client, id);

        var html = await PostAndFollowAsync(client, $"/Catalog/Products/{id}?handler=AddConversion", new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            new KeyValuePair<string, string>("ConversionInput.FromUnitId", _factory.LbUnitId.ToString()),
            new KeyValuePair<string, string>("ConversionInput.ToUnitId", _factory.KgUnitId.ToString()),
            new KeyValuePair<string, string>("ConversionInput.Factor", "0.4536"),
        });

        AssertToast(html, "Conversion added.");
    }

    [Fact(DisplayName = "RemoveConversion — toasts 'Conversion removed.'")]
    public async Task RemoveConversion_Toasts_ConversionRemoved()
    {
        var client = AuthClient();
        var id = ToastFixture.ConversionHostId;
        var token = await GetAntiforgeryTokenAsync(client, id);

        var html = await PostAndFollowAsync(
            client,
            $"/Catalog/Products/{id}?handler=RemoveConversion&conversionId={_factory.RemovableConversionId!.Value.Value}",
            new[] { new KeyValuePair<string, string>("__RequestVerificationToken", token) });

        AssertToast(html, "Conversion removed.");
    }

    [Fact(DisplayName = "PromoteConversion — toasts 'Conversion confirmed.'")]
    public async Task PromoteConversion_Toasts_ConversionConfirmed()
    {
        var client = AuthClient();
        var id = ToastFixture.ConversionHostId;
        var token = await GetAntiforgeryTokenAsync(client, id);

        var html = await PostAndFollowAsync(
            client,
            $"/Catalog/Products/{id}?handler=PromoteConversion&conversionId={_factory.PromotableConversionId!.Value.Value}",
            new[] { new KeyValuePair<string, string>("__RequestVerificationToken", token) });

        AssertToast(html, "Conversion confirmed.");
    }

    [Fact(DisplayName = "MakeVariant — toasts 'Product made a variant.'")]
    public async Task MakeVariant_Toasts_ProductMadeAVariant()
    {
        var client = AuthClient();
        var id = ToastFixture.MakeVariantProductId;
        var token = await GetAntiforgeryTokenAsync(client, id);

        var html = await PostAndFollowAsync(client, $"/Catalog/Products/{id}?handler=MakeVariant", new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            new KeyValuePair<string, string>("VariantInput.ParentProductId", ToastFixture.MakeVariantParentId.ToString()),
        });

        AssertToast(html, "Product made a variant.");
    }

    [Fact(DisplayName = "AddVariant — toasts 'Variant created.'")]
    public async Task AddVariant_Toasts_VariantCreated()
    {
        var client = AuthClient();
        var id = ToastFixture.AddVariantBaseId;
        var token = await GetAntiforgeryTokenAsync(client, id);

        var html = await PostAndFollowAsync(client, $"/Catalog/Products/{id}?handler=AddVariant", new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            new KeyValuePair<string, string>("AddVariantInput.Name", "New Variant"),
        });

        AssertToast(html, "Variant created.");
    }

    [Fact(DisplayName = "Detach — toasts 'Product detached from parent.'")]
    public async Task Detach_Toasts_ProductDetached()
    {
        var client = AuthClient();
        var id = ToastFixture.DetachProductId;
        var token = await GetAntiforgeryTokenAsync(client, id);

        var html = await PostAndFollowAsync(client, $"/Catalog/Products/{id}?handler=Detach", new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });

        AssertToast(html, "Product detached from parent.");
    }

    [Fact(DisplayName = "Archive — toasts 'Product archived.'")]
    public async Task Archive_Toasts_ProductArchived()
    {
        var client = AuthClient();
        var id = ToastFixture.ArchiveProductId;
        var token = await GetAntiforgeryTokenAsync(client, id);

        var html = await PostAndFollowAsync(client, $"/Catalog/Products/{id}?handler=Archive", new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });

        AssertToast(html, "Product archived.");
    }

    [Fact(DisplayName = "Unarchive — toasts 'Product unarchived.'")]
    public async Task Unarchive_Toasts_ProductUnarchived()
    {
        var client = AuthClient();
        var id = ToastFixture.UnarchiveProductId;
        var token = await GetAntiforgeryTokenAsync(client, id);

        var html = await PostAndFollowAsync(client, $"/Catalog/Products/{id}?handler=Unarchive", new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });

        AssertToast(html, "Product unarchived.");
    }

    [Fact(DisplayName = "A plain GET with no prior mutation renders no toast")]
    public async Task PlainGet_RendersNoToast()
    {
        var client = AuthClient();
        var html = await (await client.GetAsync($"/Catalog/Products/{ToastFixture.EditProductId}"))
            .Content.ReadAsStringAsync();

        Assert.DoesNotContain("class=\"toast\"", html);
    }

    [Fact(DisplayName = "The toast auto-clears — it does not reappear on the request after the redirect landed")]
    public async Task Toast_DoesNotPersist_PastTheOneFollowingRequest()
    {
        var client = AuthClient();
        var id = ToastFixture.ArchiveProductId2;
        var token = await GetAntiforgeryTokenAsync(client, id);

        var firstHtml = await PostAndFollowAsync(client, $"/Catalog/Products/{id}?handler=Archive", new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });
        AssertToast(firstHtml, "Product archived.");

        // TempData is read-once: a second GET of the same page must not still show the toast.
        var secondHtml = await (await client.GetAsync($"/Catalog/Products/{id}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("class=\"toast\"", secondHtml);
    }
}

// ── Fixture data ──────────────────────────────────────────────────────────────

internal static class ToastFixture
{
    internal static readonly Guid HouseholdId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    internal static readonly HouseholdId Household = Plantry.SharedKernel.HouseholdId.From(HouseholdId);
    internal static readonly IClock Clock = Plantry.SharedKernel.Domain.SystemClock.Instance;

    internal static readonly Guid EditProductId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    internal static readonly Guid SkuHostId = Guid.Parse("dddddddd-0000-0000-0000-000000000002");
    internal static readonly Guid ConversionHostId = Guid.Parse("dddddddd-0000-0000-0000-000000000003");
    internal static readonly Guid MakeVariantProductId = Guid.Parse("dddddddd-0000-0000-0000-000000000004");
    internal static readonly Guid MakeVariantParentId = Guid.Parse("dddddddd-0000-0000-0000-000000000005");
    internal static readonly Guid AddVariantBaseId = Guid.Parse("dddddddd-0000-0000-0000-000000000006");
    internal static readonly Guid DetachProductId = Guid.Parse("dddddddd-0000-0000-0000-000000000007");
    internal static readonly Guid ArchiveProductId = Guid.Parse("dddddddd-0000-0000-0000-000000000008");
    internal static readonly Guid UnarchiveProductId = Guid.Parse("dddddddd-0000-0000-0000-000000000009");
    internal static readonly Guid ArchiveProductId2 = Guid.Parse("dddddddd-0000-0000-0000-00000000000a");
}

// ── WAF factory ───────────────────────────────────────────────────────────────

/// <summary>
/// Seeds one standalone product per handler under test (see <see cref="ToastFixture"/>) so each
/// test can exercise a single handler's happy path in isolation without cross-handler state bleed.
/// </summary>
internal sealed class ToastFactory : WebApplicationFactory<Program>
{
    internal FakeProductRepo ProductRepo { get; } = new();
    internal ProductSkuId? ExistingSkuId { get; private set; }
    internal ProductConversionId? RemovableConversionId { get; private set; }
    internal ProductConversionId? PromotableConversionId { get; private set; }
    internal Guid EachUnitId { get; private set; }
    internal Guid LbUnitId { get; private set; }
    internal Guid KgUnitId { get; private set; }

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

            var each = CatalogUnit.Create(ToastFixture.Household, "ea", "Each", Dimension.Count, 1m, isBase: true);
            var lb = CatalogUnit.Create(ToastFixture.Household, "lb", "Pounds", Dimension.Mass, 453.592m, isBase: false);
            var kg = CatalogUnit.Create(ToastFixture.Household, "kg", "Kilograms", Dimension.Mass, 1000m, isBase: false);
            EachUnitId = each.Id.Value;
            LbUnitId = lb.Id.Value;
            KgUnitId = kg.Id.Value;

            var editProduct = Product.Create(ToastFixture.Household, "Edit Me", each.Id, ToastFixture.Clock);

            var skuHost = Product.Create(ToastFixture.Household, "Sku Host", each.Id, ToastFixture.Clock);
            var existingSku = skuHost.AddSku("Existing SKU", null, null, ToastFixture.Clock);
            ExistingSkuId = existingSku.Id;

            var conversionHost = Product.Create(ToastFixture.Household, "Conversion Host", each.Id, ToastFixture.Clock);
            var removable = conversionHost.AddConversion(each.Id, lb.Id, 2m, ToastFixture.Clock, ConversionSource.UserConfirmed);
            RemovableConversionId = removable.Id;
            var promotable = conversionHost.AddConversion(each.Id, kg.Id, 1m, ToastFixture.Clock, ConversionSource.AiSuggested);
            PromotableConversionId = promotable.Id;

            var makeVariantProduct = Product.Create(ToastFixture.Household, "Make Variant Me", each.Id, ToastFixture.Clock);
            var makeVariantParent = Product.Create(ToastFixture.Household, "Make Variant Parent", each.Id, ToastFixture.Clock);

            var addVariantBase = Product.Create(ToastFixture.Household, "Add Variant Base", each.Id, ToastFixture.Clock);

            var detachProduct = Product.Create(ToastFixture.Household, "Detach Me", each.Id, ToastFixture.Clock);

            var archiveProduct = Product.Create(ToastFixture.Household, "Archive Me", each.Id, ToastFixture.Clock);

            var unarchiveProduct = Product.Create(ToastFixture.Household, "Unarchive Me", each.Id, ToastFixture.Clock);
            unarchiveProduct.Archive(ToastFixture.Clock);

            var archiveProduct2 = Product.Create(ToastFixture.Household, "Archive Me Too", each.Id, ToastFixture.Clock);

            ProductRepo.AddWithId(editProduct, ToastFixture.EditProductId);
            ProductRepo.AddWithId(skuHost, ToastFixture.SkuHostId);
            ProductRepo.AddWithId(conversionHost, ToastFixture.ConversionHostId);
            ProductRepo.AddWithId(makeVariantProduct, ToastFixture.MakeVariantProductId);
            ProductRepo.AddWithId(makeVariantParent, ToastFixture.MakeVariantParentId);
            ProductRepo.AddWithId(addVariantBase, ToastFixture.AddVariantBaseId);
            ProductRepo.AddWithId(detachProduct, ToastFixture.DetachProductId);
            ProductRepo.AddWithId(archiveProduct, ToastFixture.ArchiveProductId);
            ProductRepo.AddWithId(unarchiveProduct, ToastFixture.UnarchiveProductId);
            ProductRepo.AddWithId(archiveProduct2, ToastFixture.ArchiveProductId2);

            services.RemoveAll<IProductRepository>();
            services.AddScoped<IProductRepository>(_ => ProductRepo);

            services.RemoveAll<IUnitRepository>();
            services.AddSingleton<IUnitRepository>(new FakeUnitListRepository(each, lb, kg));

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
