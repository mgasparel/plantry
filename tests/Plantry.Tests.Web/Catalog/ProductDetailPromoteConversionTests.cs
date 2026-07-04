using System.Net;
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
/// L4 Web integration tests for the AI-suggested conversion tag + Promote action on the Product
/// Detail page (plantry-3k44 / ADR-022). Proves the provenance surfaces in the rendered HTML and
/// that the Promote handler flips <see cref="ConversionSource.AiSuggested"/> to
/// <see cref="ConversionSource.UserConfirmed"/> through the aggregate root.
///
/// The catalog/inventory seams are in-memory fakes (reused from the AddVariant test file); no DB.
/// </summary>
public sealed class ProductDetailPromoteConversionTests : IDisposable
{
    private readonly PromoteConversionFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static readonly Guid HouseholdId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    private HttpClient AuthClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdId.ToString());
        return client;
    }

    private async Task<string> GetAntiforgeryTokenAsync(HttpClient client, Guid productId)
    {
        var html = await (await client.GetAsync($"/Catalog/Products/{productId}"))
            .Content.ReadAsStringAsync();
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the Detail page.");
        return match.Groups[1].Value;
    }

    [Fact(DisplayName = "Detail page tags an AI-suggested conversion and offers a Confirm action")]
    public async Task DetailPage_Renders_AiSuggested_Tag_And_Promote_Action()
    {
        var client = AuthClient();

        var html = await (await client.GetAsync($"/Catalog/Products/{PromoteConversionFactory.ProductId}"))
            .Content.ReadAsStringAsync();

        Assert.Contains("AI-suggested", html);
        // The Promote (Confirm) form posts to the PromoteConversion handler for this conversion.
        Assert.Contains("handler=PromoteConversion", html);
        Assert.Contains(_factory.ConversionId.Value.ToString(), html);
    }

    [Fact(DisplayName = "PromoteConversion flips the conversion to user-confirmed and redirects")]
    public async Task PromoteConversion_Confirms_The_Conversion()
    {
        var client = AuthClient();
        var token = await GetAntiforgeryTokenAsync(client, PromoteConversionFactory.ProductId);

        var response = await client.PostAsync(
            $"/Catalog/Products/{PromoteConversionFactory.ProductId}?handler=PromoteConversion&conversionId={_factory.ConversionId.Value}",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var product = _factory.ProductRepo.Items.Single();
        var conversion = Assert.Single(product.Conversions);
        Assert.Equal(ConversionSource.UserConfirmed, conversion.Source);

        // And the tag is gone on the next render.
        var html = await (await client.GetAsync($"/Catalog/Products/{PromoteConversionFactory.ProductId}"))
            .Content.ReadAsStringAsync();
        Assert.DoesNotContain("AI-suggested", html);
    }
}

// ── WAF factory ───────────────────────────────────────────────────────────────

internal sealed class PromoteConversionFactory : WebApplicationFactory<Program>
{
    internal static readonly Guid ProductId = Guid.Parse("aaaaaaaa-0000-0000-0000-ccc000000001");
    internal static readonly Guid HouseholdId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly HouseholdId Household = Plantry.SharedKernel.HouseholdId.From(HouseholdId);
    private static readonly IClock Clock = Plantry.SharedKernel.Domain.SystemClock.Instance;

    internal FakeProductRepo ProductRepo { get; } = new();
    internal ProductConversionId ConversionId { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            var pounds = CatalogUnit.Create(Household, "lb", "Pounds", Dimension.Mass, 453.592m, isBase: false);
            var each = CatalogUnit.Create(Household, "ea", "Each", Dimension.Count, 1m, isBase: true);

            var product = Product.Create(Household, "Bananas", each.Id, Clock);
            var conversion = product.AddConversion(pounds.Id, each.Id, 5m, Clock, ConversionSource.AiSuggested);
            ConversionId = conversion.Id;
            ProductRepo.AddWithId(product, ProductId);

            services.RemoveAll<IProductRepository>();
            services.AddScoped<IProductRepository>(_ => ProductRepo);

            services.RemoveAll<IUnitRepository>();
            services.AddSingleton<IUnitRepository>(new FakeUnitListRepository(pounds, each));

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

internal sealed class FakeUnitListRepository(params CatalogUnit[] units) : IUnitRepository
{
    private readonly List<CatalogUnit> _units = [.. units];

    public Task<CatalogUnit?> FindAsync(UnitId id, CancellationToken ct = default) =>
        Task.FromResult(_units.SingleOrDefault(u => u.Id == id));

    public Task<CatalogUnit?> FindByCodeAsync(string code, CancellationToken ct = default) =>
        Task.FromResult(_units.SingleOrDefault(u => u.Code.Equals(code, StringComparison.OrdinalIgnoreCase)));

    public Task<List<CatalogUnit>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(_units.ToList());

    public Task AddAsync(CatalogUnit u, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}
