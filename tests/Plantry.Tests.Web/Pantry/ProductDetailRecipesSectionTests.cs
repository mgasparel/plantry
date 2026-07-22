using System.Net;
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
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Pantry;

/// <summary>
/// L4 Web integration tests for the "Recipes" section on the Pantry product Detail page (plantry-o0r8) —
/// verifies the product→recipes cross-context read (<c>RecipesUsingProductQuery</c>) renders the
/// consumer/producer distinction and the empty state. Reuses the fake seams
/// <see cref="ProductDetailSetPriceTests"/> established for this page (unit/catalog/stock/pricing —
/// those internal fakes are file-scoped but assembly-visible) and adds a fake
/// <see cref="IRecipeRepository"/> for this feature's new read.
/// </summary>
public sealed class ProductDetailRecipesSectionTests : IDisposable
{
    private readonly ProductDetailRecipesSectionFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static readonly Guid HouseholdId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");

    private HttpClient AuthClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdId.ToString());
        return client;
    }

    [Fact(DisplayName = "Detail GET — shows the muted empty state when no recipe references the product")]
    public async Task Get_ShowsEmptyState_WhenNoRecipeReferencesProduct()
    {
        var client = AuthClient();

        var response = await client.GetAsync($"/Pantry/Products/Detail/{ProductDetailSetPriceFixture.ProductId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Not used in any recipes yet.", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Detail GET — lists a recipe that has a direct ingredient line as 'Used in'")]
    public async Task Get_ListsConsumerRecipe_LabelledUsedIn()
    {
        var recipe = Recipe.Create(
            ProductDetailSetPriceFixture.Household, "Chili", defaultServings: 4,
            ProductDetailSetPriceFixture.Clock).Value;
        recipe.ReplaceIngredients(
            [new IngredientLine(ProductDetailSetPriceFixture.ProductId, 1m, ProductDetailSetPriceFixture.UnitId, null, 0)],
            ProductDetailSetPriceFixture.Clock);
        _factory.RecipeRepo.Items.Add(recipe);

        var client = AuthClient();
        var response = await client.GetAsync($"/Pantry/Products/Detail/{ProductDetailSetPriceFixture.ProductId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Chili", html, StringComparison.Ordinal);
        Assert.Contains("Used in", html, StringComparison.Ordinal);
        Assert.Contains($"/Recipes/{recipe.Id.Value}", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Detail GET — lists a recipe whose declared yield targets the product as 'Made by'")]
    public async Task Get_ListsProducerRecipe_LabelledMadeBy()
    {
        var recipe = Recipe.Create(
            ProductDetailSetPriceFixture.Household, "Vegetable stock", defaultServings: 4,
            ProductDetailSetPriceFixture.Clock).Value;
        recipe.SetYield(
            ProductDetailSetPriceFixture.ProductId, 6m, ProductDetailSetPriceFixture.UnitId,
            ProductDetailSetPriceFixture.Clock);
        _factory.RecipeRepo.Items.Add(recipe);

        var client = AuthClient();
        var response = await client.GetAsync($"/Pantry/Products/Detail/{ProductDetailSetPriceFixture.ProductId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Vegetable stock", html, StringComparison.Ordinal);
        Assert.Contains("Made by", html, StringComparison.Ordinal);
        Assert.Contains($"/Recipes/{recipe.Id.Value}", html, StringComparison.Ordinal);
    }
}

// ── WAF factory ───────────────────────────────────────────────────────────────

internal sealed class ProductDetailRecipesSectionFactory : WebApplicationFactory<Program>
{
    internal FakeRecipeRepository RecipeRepo { get; } = new();

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

            services.RemoveAll<IPriceObservationRepository>();
            services.AddSingleton<IPriceObservationRepository>(new FakePriceObservationRepository());

            services.RemoveAll<IDisplayCurrency>();
            services.AddSingleton<IDisplayCurrency>(new FakeDisplayCurrency());

            services.RemoveAll<IUnitPriceCalculator>();
            services.AddSingleton<IUnitPriceCalculator>(new FakeUnitPriceCalculator(0.5m));

            // The seam this test file adds (plantry-o0r8): RecipesUsingProductQuery reads through
            // IRecipeRepository, which otherwise resolves to the real EF-backed repository and needs a
            // live Postgres connection — swap in an in-memory fake so the Recipes section can be
            // exercised DB-less like every other seam on this page.
            services.RemoveAll<IRecipeRepository>();
            services.AddSingleton<IRecipeRepository>(RecipeRepo);
        });
    }
}

/// <summary>
/// In-memory <see cref="IRecipeRepository"/> for the Recipes-section L4 tests — no household filtering
/// (household isolation is proven at the unit level in <c>RecipesUsingProductQueryTests</c>); this fake
/// only needs to prove the Web page renders what the query returns.
/// </summary>
internal sealed class FakeRecipeRepository : IRecipeRepository
{
    internal List<Recipe> Items { get; } = [];

    public Task AddAsync(Recipe recipe, CancellationToken ct = default)
    {
        Items.Add(recipe);
        return Task.CompletedTask;
    }

    public Task<Recipe?> GetByIdAsync(RecipeId id, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(r => r.Id == id));

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> NameExistsAsync(HouseholdId householdId, string name, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<Recipe>> ListForBrowseAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Recipe>>(Items.Where(r => r.ArchivedAt == null).ToList());

    public Task<IReadOnlySet<RecipeId>> ListRecipeIdsWithPhotoAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<RecipeId>>(new HashSet<RecipeId>());

    public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyDictionary<RecipeId, string>> GetRecipeNamesByIdAsync(
        IReadOnlyList<RecipeId> ids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<RecipeId, string>>(new Dictionary<RecipeId, string>());

    public Task<IReadOnlyList<RecipeInclusionEdge>> ListInclusionEdgesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RecipeInclusionEdge>>([]);

    public Task<IReadOnlySet<RecipeId>> GetIncluderIdsAsync(
        RecipeId subRecipeId, bool transitive = false, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<RecipeId>>(new HashSet<RecipeId>());
}
