using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// Handler-level tests for the yield-on-cook fields on <c>CookModel.OnPostAsync</c> (plantry-854a).
///
/// Assertions capture at the <see cref="IInventoryProducer"/> seam over a real <c>CookRecipe</c> — the
/// same "no ICookRecipe interface" pattern used by <see cref="CookOnPostResolutionTests"/> at the consumer
/// seam. The fixture recipe declares a yield (<see cref="CookConfirmFixture.YieldProductId"/> in the
/// servings unit), so the produce step fires when a positive stored quantity is posted.
/// </summary>
public sealed class CookOnPostYieldTests : IDisposable
{
    private readonly CookYieldPostFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    // The fixture recipe is 4 servings; POSTing Servings=4 means scale=1.
    private const int PostedServings = 4;

    private HttpClient AuthenticatedClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, CookConfirmFixture.HouseholdAId.ToString());
        return client;
    }

    private string CookUrl => $"/Recipes/{_factory.RecipeId}/Cook";

    private async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var html = await (await client.GetAsync($"{CookUrl}?Servings={PostedServings}"))
            .Content.ReadAsStringAsync();
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the Cook page.");
        return match.Groups[1].Value;
    }

    private async Task<HttpResponseMessage> PostCookAsync(
        HttpClient client, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var token = await GetAntiforgeryTokenAsync(client);
        var allFields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Id", _factory.RecipeId.ToString()),
            new("Servings", PostedServings.ToString()),
        };
        allFields.AddRange(fields);
        return await client.PostAsync(CookUrl, new FormUrlEncodedContent(allFields));
    }

    [Fact]
    public async Task Store_yield_quantity_and_expiry_drives_produce_with_those_values()
    {
        var client = AuthenticatedClient();

        var response = await PostCookAsync(client,
        [
            new("StoreYieldQuantity", "2"),
            new("StoreYieldExpiry", "2026-08-01"),
        ]);

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after successful cook, got {(int)response.StatusCode}.");

        var produce = Assert.Single(_factory.Producer.Calls);
        Assert.Equal(CookConfirmFixture.YieldProductId, produce.ProductId);
        Assert.Equal(2m, produce.Quantity);
        Assert.Equal(CookConfirmFixture.ServingsUnitId, produce.UnitId);
        Assert.Equal(new DateOnly(2026, 8, 1), produce.ExpiryDate); // expiry passed through
    }

    [Fact]
    public async Task Store_zero_yield_drives_no_produce_and_drops_expiry()
    {
        var client = AuthenticatedClient();

        // A zero stored quantity must add nothing — even with an expiry present in the post.
        var response = await PostCookAsync(client,
        [
            new("StoreYieldQuantity", "0"),
            new("StoreYieldExpiry", "2026-08-01"),
        ]);

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after successful cook, got {(int)response.StatusCode}.");

        Assert.Empty(_factory.Producer.Calls);
    }

    [Fact]
    public async Task No_yield_fields_posted_stores_nothing()
    {
        var client = AuthenticatedClient();

        var response = await PostCookAsync(client, []);

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after successful cook, got {(int)response.StatusCode}.");

        Assert.Empty(_factory.Producer.Calls);
    }
}

// ── WAF factory for the yield OnPost tests ───────────────────────────────────────────────────────

/// <summary>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> for the Cook POST yield tests (plantry-854a). Wires the
/// yield-declaring fixture recipe and a <see cref="RecordingFakeCookInventoryProducer"/> so tests can inspect
/// what was stored, without touching the database. Mirrors <see cref="CookPostFactory"/>'s seam replacements.
/// </summary>
internal sealed class CookYieldPostFactory : WebApplicationFactory<Program>
{
    public Recipe Recipe { get; } = CookConfirmFixture.BuildWithYield();
    public Guid RecipeId => Recipe.Id.Value;

    public RecordingFakeCookInventoryProducer Producer { get; } = new();

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

            services.RemoveAll<IRecipeRepository>();
            services.AddScoped<IRecipeRepository>(sp =>
                new FakeRecipeRepository(sp.GetRequiredService<ITenantContext>(), Recipe));

            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeCookCatalogReader(CookConfirmFixture.ProductsWithYield(), CookConfirmFixture.UnitCodesWithYield()));

            services.RemoveAll<IInventoryStockReader>();
            services.AddSingleton<IInventoryStockReader>(
                new FakeCookStockReader(CookConfirmFixture.Stock()));

            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeCookUnitConverter());
            services.AddFakeQuantityFormatter();

            services.RemoveAll<IInventoryConsumer>();
            services.AddSingleton<IInventoryConsumer>(new FakeCookInventoryConsumer());

            // The seam under test: record produces instead of hitting the real AddStock path.
            services.RemoveAll<IInventoryProducer>();
            services.AddSingleton<IInventoryProducer>(Producer);

            services.RemoveAll<ICookEventRepository>();
            services.AddSingleton<ICookEventRepository>(new FakeCookEventRepository());

            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(new FakeTagRepository(new Dictionary<TagId, string>()));

            services.RemoveAll<IPriceReader>();
            services.AddSingleton<IPriceReader>(new FakeDetailPriceReader(new Dictionary<Guid, PricePoint>()));
        });
    }
}
