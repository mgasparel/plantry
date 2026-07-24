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
/// Handler-level tests for the no-yield-product gap (plantry-iejb): a recipe with no declared yield
/// gets a just-in-time product resolved at cook-confirm time — either the one-tap
/// "«Recipe» (leftovers)" auto-create default, or an existing product the user picked via
/// <c>PickedYieldProductId</c> — and it is persisted onto the recipe (<see cref="Recipe.YieldProductId"/>)
/// so the next cook shows the normal declared-yield block with no prompt. A zero stored quantity
/// creates and persists nothing, exactly like today's declared-yield zero-quantity path.
///
/// Assertions capture at the <see cref="RecordingFakeCookInventoryProducer"/> / <see cref="FakeCatalogWriter"/>
/// seams over a real <c>CookRecipe</c> — same "no ICookRecipe interface" pattern as
/// <see cref="CookOnPostYieldTests"/>. The fixture <see cref="Recipe"/> is a single shared instance
/// (mirrors <see cref="Infrastructure.FakeRecipeRepository"/>'s in-memory semantics), so a SetYield
/// mutation during the POST is directly observable afterwards.
/// </summary>
public sealed class CookOnPostYieldCreationTests : IDisposable
{
    private readonly CookNoYieldPostFactory _factory = new();

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
    public async Task Zero_Stored_Quantity_Creates_Nothing_And_Persists_Nothing()
    {
        var client = AuthenticatedClient();

        var response = await PostCookAsync(client,
        [
            new("StoreYieldQuantity", "0"),
        ]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Empty(_factory.Producer.Calls);
        Assert.Empty(_factory.CatalogWriter.TrackedProductsCreated);
        Assert.Null(_factory.Recipe.YieldProductId);
    }

    [Fact]
    public async Task Positive_Stored_Quantity_With_No_Pick_AutoCreates_Leftovers_Product()
    {
        var client = AuthenticatedClient();

        var response = await PostCookAsync(client,
        [
            new("StoreYieldQuantity", "3"),
            new("StoreYieldExpiry", "2026-08-01"),
        ]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var created = Assert.Single(_factory.CatalogWriter.TrackedProductsCreated);
        Assert.Equal($"{_factory.Recipe.Name} (leftovers)", created.Name);
        Assert.Equal(CookConfirmFixture.EachUnitId, created.DefaultUnitId);

        var produceCall = Assert.Single(_factory.Producer.Calls);
        Assert.Equal(3m, produceCall.Quantity);
        Assert.Equal(CookConfirmFixture.EachUnitId, produceCall.UnitId);
        Assert.Equal(new DateOnly(2026, 8, 1), produceCall.ExpiryDate);

        // Persisted onto the recipe — the next cook shows the normal declared-yield block, no prompt.
        Assert.Equal(produceCall.ProductId, _factory.Recipe.YieldProductId);
        Assert.Equal(CookConfirmFixture.EachUnitId, _factory.Recipe.YieldUnitId);
        Assert.True(_factory.Recipe.HasYield);
    }

    [Fact]
    public async Task Positive_Stored_Quantity_With_Picked_Product_Stores_Under_It_Not_A_New_One()
    {
        var client = AuthenticatedClient();

        // Garlic (GarlicParentId) is a parent, not a valid pick target for stock; use a concrete
        // tracked leaf from the fixture instead — Pasta is tracked and holds stock directly.
        var response = await PostCookAsync(client,
        [
            new("StoreYieldQuantity", "2"),
            new("PickedYieldProductId", CookConfirmFixture.PastaId.ToString()),
        ]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        Assert.Empty(_factory.CatalogWriter.TrackedProductsCreated);

        var produceCall = Assert.Single(_factory.Producer.Calls);
        Assert.Equal(CookConfirmFixture.PastaId, produceCall.ProductId);
        Assert.Equal(CookConfirmFixture.GramUnitId, produceCall.UnitId); // Pasta's own default unit
        Assert.Equal(2m, produceCall.Quantity);

        Assert.Equal(CookConfirmFixture.PastaId, _factory.Recipe.YieldProductId);
        Assert.Equal(CookConfirmFixture.GramUnitId, _factory.Recipe.YieldUnitId);
    }
}

// ── WAF factory for the no-yield-product creation OnPost tests ────────────────────────────────────

/// <summary>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> for the no-yield-product-gap OnPost tests
/// (plantry-iejb). Uses the plain <see cref="CookConfirmFixture.Build"/> recipe (no declared yield) and
/// wires <see cref="CookConfirmFixture.Units"/> so CookRecipe's just-in-time creation can resolve the
/// household's "ea" count unit. Mirrors <see cref="CookYieldPostFactory"/>'s seam replacements.
/// </summary>
internal sealed class CookNoYieldPostFactory : WebApplicationFactory<Program>
{
    public Recipe Recipe { get; } = CookConfirmFixture.Build();
    public Guid RecipeId => Recipe.Id.Value;

    public RecordingFakeCookInventoryProducer Producer { get; } = new();
    public FakeCatalogWriter CatalogWriter { get; } = new();

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
                new FakeCookCatalogReader(CookConfirmFixture.Products(), CookConfirmFixture.UnitCodes(), CookConfirmFixture.Units()));

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

            // The seam under test: record just-in-time yield-product creations (plantry-iejb).
            services.RemoveAll<ICatalogWriter>();
            services.AddSingleton<ICatalogWriter>(CatalogWriter);

            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(new FakeTagRepository(new Dictionary<TagId, string>()));

            services.RemoveAll<IPriceReader>();
            services.AddSingleton<IPriceReader>(new FakeDetailPriceReader(new Dictionary<Guid, PricePoint>()));
        });
    }
}
