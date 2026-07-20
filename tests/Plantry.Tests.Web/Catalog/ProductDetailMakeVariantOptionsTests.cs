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
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Web.Infrastructure;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Web.Catalog;

/// <summary>
/// L4 Web integration regression tests for the "Make a variant of" dropdown on the Product
/// Detail page (plantry-kr7i). The dropdown must offer any active, non-variant product —
/// <em>including products that are already parents</em> — so a second (and third, etc.) variant
/// can be attached to the same parent, matching <c>MakeVariantCommand</c>'s actual domain rule.
///
/// The catalog / inventory seams are replaced by in-memory fakes; no database is touched.
/// Product route ids are the products' real domain ids so the page's <c>p.Id != Id</c>
/// self-exclusion behaves exactly as in production.
/// </summary>
public sealed class ProductDetailMakeVariantOptionsTests : IDisposable
{
    private readonly MakeVariantOptionsFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static readonly Guid HouseholdId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    private HttpClient AuthClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdId.ToString());
        return client;
    }

    /// <summary>Extracts the inner HTML of the "Make a variant of" select so option assertions
    /// are scoped to the dropdown and not confused by ids appearing elsewhere on the page.</summary>
    private static string ExtractParentSelect(string html)
    {
        var match = Regex.Match(
            html,
            "name=\"VariantInput\\.ParentProductId\".*?</select>",
            RegexOptions.Singleline);
        Assert.True(match.Success, "The 'Make a variant of' select was not rendered on the Detail page.");
        return match.Value;
    }

    [Fact(DisplayName = "MakeVariant dropdown — lists an existing parent product as an eligible parent")]
    public async Task MakeVariantDropdown_IncludesExistingParent_ExcludesVariantsAndSelf()
    {
        var client = AuthClient();

        var html = await (await client.GetAsync($"/Catalog/Products/{_factory.StandaloneId}"))
            .Content.ReadAsStringAsync();

        var select = ExtractParentSelect(html);

        // Regression: a product that is already a parent (has >=1 variant) must still be offered.
        Assert.Contains(_factory.ParentId.ToString(), select);

        // A variant may never itself be selected as a parent (max depth 1).
        Assert.DoesNotContain(_factory.VariantId.ToString(), select);

        // The product being viewed must not be offered as its own parent.
        Assert.DoesNotContain(_factory.StandaloneId.ToString(), select);
    }
}

// ── WAF factory ───────────────────────────────────────────────────────────────

/// <summary>
/// L4 factory seeding four products keyed by their real domain ids: the standalone product
/// being viewed, an existing parent (has a variant), that parent's variant, and a spare
/// standalone. Reuses the in-memory fakes defined alongside the "Add a variant" tests.
/// </summary>
internal sealed class MakeVariantOptionsFactory : WebApplicationFactory<Program>
{
    internal Guid StandaloneId { get; private set; }
    internal Guid ParentId { get; private set; }
    internal Guid VariantId { get; private set; }

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

            var household = Plantry.SharedKernel.HouseholdId.From(
                Guid.Parse("cccccccc-0000-0000-0000-000000000001"));
            var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
            var unit = CatalogUnit.Create(household, "ea", "Each", Dimension.Count, 1m, isBase: true);

            // The product whose detail page we view — standalone, so the dropdown renders.
            var standalone = Product.Create(household, "Bubly Orange", unit.Id, clock);
            // An existing parent: it already has a variant, so IsParent is true.
            var parent = Product.Create(household, "Bubly", unit.Id, clock);
            parent.SetHasVariants(true, clock);
            // The parent's variant — must never be offered as a parent itself.
            var variant = Product.Create(household, "Bubly Blueberry Pomegranate", unit.Id, clock);
            variant.MakeVariantOf(parent.Id, clock);

            StandaloneId = standalone.Id.Value;
            ParentId = parent.Id.Value;
            VariantId = variant.Id.Value;

            var productRepo = new FakeProductRepo();
            // Key each product by its real domain id so the page route id matches product.Id.
            productRepo.AddWithId(standalone, StandaloneId);
            productRepo.AddWithId(parent, ParentId);
            productRepo.AddWithId(variant, VariantId);

            services.RemoveAll<IProductRepository>();
            services.AddScoped<IProductRepository>(_ => productRepo);

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
