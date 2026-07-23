using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
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
/// Cook-page test for plantry-cbww: an untracked ingredient (<c>Product.TrackStock == false</c>) with a
/// REAL authored quantity/unit must still display that amount alongside the "untracked" sub-label,
/// instead of being suppressed purely because it is untracked. A separate untracked ingredient with a
/// genuinely null quantity/unit ("to taste") must keep rendering with no amount, unchanged — pinned here
/// alongside the has-quantity case so a regression in either direction fails this suite.
/// </summary>
public sealed class CookUntrackedQuantityTests(CookUntrackedQuantityFactory factory)
    : IClassFixture<CookUntrackedQuantityFactory>
{
    private static readonly HtmlParser Parser = new();

    private async Task<string> GetCookPageAsync()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, CookUntrackedQuantityFixture.HouseholdGuid.ToString());
        var response = await client.GetAsync($"/Recipes/{factory.RecipeId}/Cook?Servings=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static IElement RowFor(string pageHtml, string productName)
    {
        var doc = Parser.ParseDocument(pageHtml);
        return doc.QuerySelectorAll(".cook-ing-row")
                   .FirstOrDefault(r => r.TextContent.Contains(productName, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"No cook-ing-row found for '{productName}'.");
    }

    [Fact]
    public async Task Untracked_ingredient_with_real_quantity_shows_the_amount_and_untracked_label()
    {
        var html = await GetCookPageAsync();
        var row = RowFor(html, "Garlic Cloves");

        Assert.Contains("cook-ing-row--untracked", row.ClassList);
        var sub = row.QuerySelector(".cook-ing-row__sub")
            ?? throw new InvalidOperationException("Sub-label not found on the untracked row.");
        Assert.Contains("untracked", sub.TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2", sub.TextContent, StringComparison.Ordinal);
        Assert.Contains("ea", sub.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Untracked_ingredient_with_null_quantity_still_renders_to_taste_with_no_amount()
    {
        var html = await GetCookPageAsync();
        var row = RowFor(html, "Basil");

        var sub = row.QuerySelector(".cook-ing-row__sub")
            ?? throw new InvalidOperationException("Sub-label not found on the untracked row.");
        Assert.Equal("to taste", sub.TextContent.Trim());
    }
}

// ── Fixture + fakes ──────────────────────────────────────────────────────────────────────────────

/// <summary>Fixture data for the Cook untracked-quantity tests (plantry-cbww).</summary>
public static class CookUntrackedQuantityFixture
{
    public static readonly Guid HouseholdGuid = Guid.Parse("e1e1e1e1-0000-0000-0000-000000000001");

    public static readonly RecipeId RecipeId = Plantry.Recipes.Domain.RecipeId.From(
        Guid.Parse("e1e1e1e1-0000-0000-0000-000000000002"));

    // Untracked, real quantity (2 ea) — plantry-cbww's repro.
    public static readonly Guid GarlicId = Guid.Parse("e2222222-2222-2222-2222-222222222222");
    // Untracked, genuinely null quantity ("to taste") — must render unchanged.
    public static readonly Guid BasilId = Guid.Parse("e3333333-3333-3333-3333-333333333333");

    public static readonly Guid EachUnitId = Guid.Parse("eeeeeeee-1111-0000-0000-000000000001");

    public static Recipe Build()
    {
        var hid = HouseholdId.From(HouseholdGuid);
        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
        var recipe = Recipe.Create(hid, "Herb Bowl", defaultServings: 1, clock).Value;
        recipe.ReplaceIngredients(
        [
            new IngredientLine(GarlicId, 2m, EachUnitId, GroupHeading: null, Ordinal: 1),
            new IngredientLine(BasilId, Quantity: null, UnitId: null, GroupHeading: null, Ordinal: 2),
        ], clock);
        return recipe;
    }

    public static IReadOnlyDictionary<Guid, CatalogProduct> Products() =>
        new Dictionary<Guid, CatalogProduct>
        {
            [GarlicId] = new(GarlicId, "Garlic Cloves", TrackStock: false, EachUnitId, null, IsParent: false, []),
            [BasilId] = new(BasilId, "Basil", TrackStock: false, EachUnitId, null, IsParent: false, []),
        };

    public static IReadOnlyDictionary<Guid, string> UnitCodes() =>
        new Dictionary<Guid, string> { [EachUnitId] = "ea" };
}

/// <summary>WAF for the Cook untracked-quantity tests — real Plantry.Web pipeline, in-memory seams.</summary>
public sealed class CookUntrackedQuantityFactory : WebApplicationFactory<Program>
{
    public Recipe Recipe { get; } = CookUntrackedQuantityFixture.Build();
    public Guid RecipeId => Recipe.Id.Value;

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
                new FakeCookCatalogReader(CookUntrackedQuantityFixture.Products(), CookUntrackedQuantityFixture.UnitCodes()));

            services.RemoveAll<IInventoryStockReader>();
            services.AddSingleton<IInventoryStockReader>(
                new FakeCookStockReader(new Dictionary<Guid, ProductStock>()));

            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeDetailUnitConverter());
            services.AddFakeQuantityFormatter();

            services.RemoveAll<IInventoryConsumer>();
            services.AddSingleton<IInventoryConsumer>(new FakeCookInventoryConsumer());

            services.RemoveAll<ICookEventRepository>();
            services.AddSingleton<ICookEventRepository>(new FakeCookEventRepository());

            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(new FakeTagRepository(new Dictionary<TagId, string>()));

            services.RemoveAll<IPriceReader>();
            services.AddSingleton<IPriceReader>(new FakeDetailPriceReader(new Dictionary<Guid, PricePoint>()));
        });
    }
}
