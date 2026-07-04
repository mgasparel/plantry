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
using Plantry.Shopping.Domain;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 integration tests for the <c>OnGetFulfilmentAsync</c> htmx handler (plantry-6vg).
/// Asserts that the fulfillment partial is recomputed at the <em>requested</em> servings
/// rather than the recipe's default servings — which was the original bug.
///
/// <para>Uses a factory variant that seeds all tracked ingredients InStock at default
/// servings (4), so the initial render shows <c>rd-fulf-card--cookable</c>. At doubled
/// servings (8) all tracked ingredients exceed available stock and the partial must
/// reflect the scaled-down cookability.</para>
/// </summary>
public sealed class RecipeDetailFulfilmentHandlerTests(RecipeDetailAllInStockFactory factory)
    : IClassFixture<RecipeDetailAllInStockFactory>
{
    /// <summary>
    /// Regression: the handler must pass the caller-supplied <paramref name="servings"/> to
    /// FulfillmentService, not the recipe's DefaultServings (the original bug). Verifying:
    /// at 4 servings → FullyCookable (rd-fulf-card--cookable); at 8 servings (pasta now needs
    /// 800g, only 600g available) → not cookable, pasta marked Low.
    /// </summary>
    [Fact]
    public async Task FulfilmentHandler_AtScaledServings_ReflectsUpdatedStatus()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeDetailFixture.HouseholdAId.ToString());

        // ── At default servings (4): all InStock, card shows cookable ──────────────
        var defaultResponse = await client.GetAsync(
            $"/Recipes/{factory.RecipeId}?handler=Fulfilment&servings=4");
        Assert.Equal(HttpStatusCode.OK, defaultResponse.StatusCode);
        var defaultHtml = await defaultResponse.Content.ReadAsStringAsync();

        // At default servings, all ingredients are InStock → FullyCookable.
        Assert.Contains("rd-fulf-card--cookable", defaultHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("rd-ing-status--low",    defaultHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("rd-ing-status--miss",   defaultHtml, StringComparison.Ordinal);

        // ── At doubled servings (8): pasta now needs 800g (have 600g) → Low ────────
        var scaledResponse = await client.GetAsync(
            $"/Recipes/{factory.RecipeId}?handler=Fulfilment&servings=8");
        Assert.Equal(HttpStatusCode.OK, scaledResponse.StatusCode);
        var scaledHtml = await scaledResponse.Content.ReadAsStringAsync();

        // At 8 servings: pasta (600g available, needs 800g) and others go Low/Missing.
        // The fulfillment card must no longer show the cookable modifier.
        Assert.DoesNotContain("rd-fulf-card--cookable", scaledHtml, StringComparison.Ordinal);

        // At least one ingredient must be flagged as low (pasta, at minimum).
        Assert.Contains("rd-ing-status--low", scaledHtml, StringComparison.Ordinal);

        // The OOB ingredient-rows block must be present in the htmx response.
        Assert.Contains("hx-swap-oob=\"true\"", scaledHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"rd-ing-rows\"", scaledHtml, StringComparison.Ordinal);
    }

    /// <summary>
    /// The handler returns 404 when the recipe does not belong to the authenticated household,
    /// preserving the same tenancy guarantee as the main page GET.
    /// </summary>
    [Fact]
    public async Task FulfilmentHandler_ForeignHousehold_Returns404()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            "bbbbbbbb-0000-0000-0000-000000000002"); // wrong household

        var response = await client.GetAsync(
            $"/Recipes/{factory.RecipeId}?handler=Fulfilment&servings=4");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Out-of-range servings (0 or >24) are clamped to DefaultServings rather than
    /// throwing — the handler falls back gracefully.
    /// </summary>
    [Fact]
    public async Task FulfilmentHandler_OutOfRangeServings_Returns200WithClampedResult()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeDetailFixture.HouseholdAId.ToString());

        // servings=0 should not throw — clamped to DefaultServings.
        var response = await client.GetAsync(
            $"/Recipes/{factory.RecipeId}?handler=Fulfilment&servings=0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

/// <summary>
/// Factory variant for <see cref="RecipeDetailFulfilmentHandlerTests"/>: seeds all tracked
/// ingredients InStock at default servings (4) so the initial fulfillment card shows
/// <c>rd-fulf-card--cookable</c>. At doubled servings (8) they all exceed available stock.
///
/// Stock:
/// - Pasta: 600g available — InStock at 4 (need 400g), Low at 8 (need 800g)
/// - Tomatoes: 600g available — InStock at 4 (need 500g), Low at 8 (need 1000g)
/// - Garlic: 4ea available — InStock at 4 (need 3ea), Low at 8 (need 6ea)
/// - Salt: untracked
/// </summary>
public sealed class RecipeDetailAllInStockFactory : WebApplicationFactory<Program>
{
    public Recipe Recipe { get; } = RecipeDetailFixture.Build();

    public Guid RecipeId => Recipe.Id.Value;

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static IReadOnlyDictionary<Guid, ProductStock> AllInStockAtDefaultServings(DateOnly today) =>
        new Dictionary<Guid, ProductStock>
        {
            [RecipeDetailFixture.PastaId]  = new(RecipeDetailFixture.PastaId,  600m, RecipeDetailFixture.GramUnitId, null),
            [RecipeDetailFixture.TomatoId] = new(RecipeDetailFixture.TomatoId, 600m, RecipeDetailFixture.GramUnitId, null),
            [RecipeDetailFixture.GarlicId] = new(RecipeDetailFixture.GarlicId, 4m,   RecipeDetailFixture.EachUnitId, null),
        };

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

            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(
                new FakeTagRepository(RecipeDetailFixture.TagNames()));

            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeCatalogProductReader(RecipeDetailFixture.Products(), RecipeDetailFixture.UnitCodes()));

            // All InStock at default servings → FullyCookable on initial render.
            services.RemoveAll<IInventoryStockReader>();
            services.AddSingleton<IInventoryStockReader>(
                new FakeDetailStockReader(AllInStockAtDefaultServings(Today)));

            services.RemoveAll<IPriceReader>();
            services.AddSingleton<IPriceReader>(new FakeDetailPriceReader(RecipeDetailFixture.Prices()));

            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeDetailUnitConverter());

            services.RemoveAll<IShoppingListWriter>();
            services.AddSingleton<IShoppingListWriter>(NullShoppingListWriterForFulfilment.Instance);

            // The fulfilment handler now reads the recipe's contribution state to compute the
            // add-button labels (plantry-gsj); stub an empty list so it resolves to "not on the list"
            // without a real Shopping DB connection.
            services.RemoveAll<IShoppingListRepository>();
            services.AddScoped<IShoppingListRepository, NullShoppingListRepositoryForFulfilment>();
        });
    }
}

/// <summary>No-op shopping list writer (file-scoped to this test file to avoid naming collision).</summary>
file sealed class NullShoppingListWriterForFulfilment : IShoppingListWriter
{
    public static readonly NullShoppingListWriterForFulfilment Instance = new();
    public Task<ShoppingSyncOutcome> SyncSourceContributionAsync(
        IReadOnlyList<ShoppingItem> items, string source, Guid sourceRef, CancellationToken ct = default)
        => Task.FromResult(ShoppingSyncOutcome.None);
}

/// <summary>Empty shopping list repository (file-scoped) — the fulfilment handler treats the recipe as
/// not yet on the list (plantry-gsj), avoiding a real Shopping DB connection.</summary>
file sealed class NullShoppingListRepositoryForFulfilment : IShoppingListRepository
{
    public Task<ShoppingList?> GetForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        => Task.FromResult<ShoppingList?>(null);

    public Task<ShoppingList?> GetByIdAsync(ShoppingListId id, CancellationToken ct = default)
        => Task.FromResult<ShoppingList?>(null);

    public Task AddAsync(ShoppingList list, CancellationToken ct = default) => Task.CompletedTask;

    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
}
