using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Catalog.Domain;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Shopping.Domain;
using Plantry.Tests.Web.Infrastructure;
using CatalogUnit = Plantry.Catalog.Domain.Unit;
using RecipesProductStock = Plantry.Recipes.Application.ProductStock;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 wiring assertions proving <see cref="QuantityDisplay"/> is actually reached by the Details and Cook
/// render surfaces (quantity-display.md §7 / §8 — "one rendering assertion each on Details and Cook proving
/// the formatter is wired"). The formatter's own behaviour is pinned exhaustively at the domain layer
/// (<c>QuantityDisplayTests</c>, plantry-vci8.2); these two tests only prove the pages call it with the
/// household's real (Fraction-styled, US-customary) units so a fraction/simplification survives to the DOM.
///
/// Fixture: cup/tbsp/tsp seeded at the nutrition-label factors (240 / 15 / 5 — plantry-5yde), all
/// Fraction-styled and UsCustomary so simplification may cross between them. A recipe (4 servings) with a
/// 0.5 cup ingredient (Butter) and a 2 tbsp ingredient (Oil).
/// </summary>
public sealed class QuantityDisplayWiringTests
{
    // ── Details renders the authored amount as a vulgar fraction at 1× (Q1) ──────────────────────────
    [Fact]
    public async Task Details_RendersAuthoredHalfCupAsFraction()
    {
        using var factory = new QuantityDisplayWiringFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, QuantityDisplayWiringFactory.HouseholdId.ToString());

        var raw = await (await client.GetAsync($"/Recipes/{factory.RecipeId}")).Content.ReadAsStringAsync();
        var html = System.Net.WebUtility.HtmlDecode(raw);

        // 0.5 cup renders as "½" (glyph) in the ingredient amount, not the "0.5" decimal.
        Assert.Contains("½", html);
    }

    // ── Cook at 2× simplifies the machine-computed default (4 tbsp → ¼ cup) then formats it (Q2) ──────
    [Fact]
    public async Task Cook_At2x_SimplifiesTwoTbspToQuarterCup()
    {
        using var factory = new QuantityDisplayWiringFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, QuantityDisplayWiringFactory.HouseholdId.ToString());

        // Default servings = 4; request 8 → scale 2 → Oil's 2 tbsp becomes 4 tbsp → simplifies to ¼ cup.
        var raw = await (await client.GetAsync($"/Recipes/{factory.RecipeId}/Cook?Servings=8"))
            .Content.ReadAsStringAsync();
        var html = System.Net.WebUtility.HtmlDecode(raw);

        Assert.Contains("¼ cup", html);
    }
}

/// <summary>
/// L4 WebApplicationFactory for the quantity-display wiring tests. Boots the real Plantry.Web pipeline with
/// every Postgres-backed seam replaced by an in-memory fake — including a <see cref="FakeQuantityFormatter"/>
/// over the fixture's real Fraction-styled units, so the live formatting logic runs without a database.
/// </summary>
public sealed class QuantityDisplayWiringFactory : WebApplicationFactory<Program>
{
    public static readonly Guid HouseholdId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    private static readonly Guid ButterId = Guid.Parse("cccccccc-1111-0000-0000-000000000001");
    private static readonly Guid OilId = Guid.Parse("cccccccc-2222-0000-0000-000000000002");

    // Fraction-styled US-customary volume units at the nutrition-label factors (cup 240 : tbsp 15 : tsp 5).
    private static readonly CatalogUnit Cup = MakeUnit("cup", 240m, isBase: false);
    private static readonly CatalogUnit Tbsp = MakeUnit("tbsp", 15m, isBase: false);
    private static readonly CatalogUnit Tsp = MakeUnit("tsp", 5m, isBase: false);
    private static readonly IReadOnlyList<CatalogUnit> Units = [Cup, Tbsp, Tsp];

    public Recipe Recipe { get; } = BuildRecipe();
    public Guid RecipeId => Recipe.Id.Value;

    private static CatalogUnit MakeUnit(string code, decimal factorToBase, bool isBase)
    {
        var unit = CatalogUnit.Create(
            Plantry.SharedKernel.HouseholdId.From(HouseholdId), code, code,
            Dimension.Volume, factorToBase, isBase, UnitSystem.UsCustomary);
        unit.SetDisplayStyle(DisplayStyle.Fraction);
        return unit;
    }

    private static Recipe BuildRecipe()
    {
        var hid = Plantry.SharedKernel.HouseholdId.From(HouseholdId);
        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
        var recipe = Recipe.Create(hid, "Fraction Test Bake", defaultServings: 4, clock).Value;
        recipe.ReplaceIngredients(
        [
            new IngredientLine(ButterId, 0.5m, Cup.Id.Value, GroupHeading: null, Ordinal: 1),
            new IngredientLine(OilId, 2m, Tbsp.Id.Value, GroupHeading: null, Ordinal: 2),
        ], clock);
        return recipe;
    }

    private static IReadOnlyDictionary<Guid, CatalogProduct> Products() =>
        new Dictionary<Guid, CatalogProduct>
        {
            [ButterId] = new(ButterId, "Butter", TrackStock: true, Cup.Id.Value, null, IsParent: false, []),
            [OilId] = new(OilId, "Olive Oil", TrackStock: true, Tbsp.Id.Value, null, IsParent: false, []),
        };

    private static IReadOnlyDictionary<Guid, string> UnitCodes() =>
        new Dictionary<Guid, string>
        {
            [Cup.Id.Value] = "cup",
            [Tbsp.Id.Value] = "tbsp",
            [Tsp.Id.Value] = "tsp",
        };

    private static IReadOnlyDictionary<Guid, RecipesProductStock> Stock() =>
        new Dictionary<Guid, RecipesProductStock>
        {
            [ButterId] = new(ButterId, 1m, Cup.Id.Value, null),
            [OilId] = new(OilId, 4m, Tbsp.Id.Value, null),
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
            services.AddSingleton<ITagRepository>(new FakeTagRepository(new Dictionary<TagId, string>()));

            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(new FakeCatalogProductReader(Products(), UnitCodes()));

            services.RemoveAll<IInventoryStockReader>();
            services.AddSingleton<IInventoryStockReader>(new FakeDetailStockReader(Stock()));

            services.RemoveAll<IPriceReader>();
            services.AddSingleton<IPriceReader>(new FakeDetailPriceReader(new Dictionary<Guid, PricePoint>()));

            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeDetailUnitConverter());

            // The formatter over the fixture's real Fraction units — the seam under test.
            services.AddFakeQuantityFormatter(Units);

            // Cook-page write seams (GET only exercises the render, but the graph must resolve).
            services.RemoveAll<IInventoryConsumer>();
            services.AddSingleton<IInventoryConsumer>(new FakeCookInventoryConsumer());
            services.RemoveAll<ICookEventRepository>();
            services.AddSingleton<ICookEventRepository>(new FakeCookEventRepository());

            // Shopping seams the Detail fulfilment card touches — nulled so no Shopping DB is needed.
            services.RemoveAll<IShoppingListWriter>();
            services.AddSingleton<IShoppingListWriter>(NullShoppingWriter.Instance);
            services.RemoveAll<IShoppingListRepository>();
            services.AddScoped<IShoppingListRepository, NullShoppingRepository>();
        });
    }
}

/// <summary>No-op shopping list writer (file-scoped) so the Detail fulfilment card needs no Shopping DB.</summary>
file sealed class NullShoppingWriter : IShoppingListWriter
{
    public static readonly NullShoppingWriter Instance = new();
    public Task<ShoppingSyncOutcome> SyncSourceContributionAsync(
        IReadOnlyList<ShoppingItem> items, string source, Guid sourceRef, CancellationToken ct = default)
        => Task.FromResult(ShoppingSyncOutcome.None);
}

/// <summary>Empty shopping list repository (file-scoped) — the recipe is treated as not on the list.</summary>
file sealed class NullShoppingRepository : IShoppingListRepository
{
    public Task<ShoppingList?> GetForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        => Task.FromResult<ShoppingList?>(null);

    public Task<ShoppingList?> GetByIdAsync(ShoppingListId id, CancellationToken ct = default)
        => Task.FromResult<ShoppingList?>(null);

    public Task AddAsync(ShoppingList list, CancellationToken ct = default) => Task.CompletedTask;

    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
}
