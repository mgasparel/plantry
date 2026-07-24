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
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// Cook-page unit-gap display tests (plantry-qll2.5).
///
/// When a leaf ingredient's recipe unit can't be converted to the product's stock unit, the Cook
/// page used to render it identically to an empty pantry ("have 0 / need 1 cup") — misleading
/// precisely when the user is holding a full bag. These tests pin the distinct treatment:
///
///   • Cashews  — needs 1 cup, stocked 480 g, NO g↔cup conversion → UNIT GAP (info tone, real
///                on-hand amount, "Add conversion" link). Not a shortfall.
///   • Flour    — needs 200 g, NO stock at all               → TRUE ZERO shortfall (warning tone).
///   • Milk     — needs 1 cup, stocked 1000 ml, HAS a ml↔cup conversion → normal availability, no gap.
///
/// This bead changes DISPLAY only; the POST/consume path is unchanged (the last test confirms a
/// cook with a unit gap still proceeds).
/// </summary>
public sealed class CookUnitGapTests(CookUnitGapFactory factory)
    : IClassFixture<CookUnitGapFactory>
{
    private static readonly HtmlParser Parser = new();

    private async Task<string> GetCookPageAsync()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, CookUnitGapFixture.HouseholdGuid.ToString());
        var response = await client.GetAsync($"/Recipes/{factory.RecipeId}/Cook?Servings=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Returns the .cook-ing-row element whose visible text contains the product name.</summary>
    private static IElement RowFor(string pageHtml, string productName)
    {
        var doc = Parser.ParseDocument(pageHtml);
        return doc.QuerySelectorAll(".cook-ing-row")
                   .FirstOrDefault(r => r.TextContent.Contains(productName, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"No cook-ing-row found for '{productName}'.");
    }

    // ── AC1: unit gap gets its own treatment with the real on-hand amount + Add conversion link ──

    [Fact]
    public async Task Unit_gap_line_shows_info_treatment_with_real_on_hand_amount()
    {
        var html = await GetCookPageAsync();
        var row = RowFor(html, "Cashews");

        // Info-tone status dot, not the warning shortfall dot.
        Assert.NotNull(row.QuerySelector(".cook-ing-status--unitgap"));
        Assert.Null(row.QuerySelector(".cook-ing-status--shortfall"));

        // The unit-gap tag carries the REAL on-hand amount in the stock unit — not "have 0".
        var tag = row.QuerySelector(".cook-unitgap-tag");
        Assert.NotNull(tag);
        Assert.Contains("480 g on hand", tag!.TextContent, StringComparison.Ordinal);
        Assert.Contains("no cup↔g conversion", tag.TextContent, StringComparison.Ordinal);

        // The misleading shortfall tag must NOT be present on a unit-gap line.
        Assert.Null(row.QuerySelector(".cook-shortfall-tag"));
    }

    [Fact]
    public async Task Unit_gap_line_links_to_product_page_to_add_conversion()
    {
        var html = await GetCookPageAsync();
        var row = RowFor(html, "Cashews");

        var link = row.QuerySelector("a.cook-unitgap-link");
        Assert.NotNull(link);
        Assert.Equal($"/Catalog/Products/{CookUnitGapFixture.CashewId}",
            link!.GetAttribute("href"));
        Assert.Contains("Add conversion", link.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unit_gap_notice_present_in_info_tone()
    {
        var html = await GetCookPageAsync();
        Assert.Contains("cook-unitgap-notice", html, StringComparison.Ordinal);
    }

    // ── AC2: true zero stock still shows the existing warning shortfall treatment ─────────────────

    [Fact]
    public async Task True_zero_stock_still_shows_shortfall_treatment()
    {
        var html = await GetCookPageAsync();
        var row = RowFor(html, "Flour");

        // Warning shortfall dot + "have 0 / need 200 g" tag — unchanged behaviour.
        Assert.NotNull(row.QuerySelector(".cook-ing-status--shortfall"));
        var tag = row.QuerySelector(".cook-shortfall-tag");
        Assert.NotNull(tag);
        Assert.Contains("need 200", tag!.TextContent, StringComparison.Ordinal);

        // A true zero is NOT a unit gap.
        Assert.Null(row.QuerySelector(".cook-unitgap-tag"));
        Assert.Null(row.QuerySelector(".cook-ing-status--unitgap"));
    }

    // ── AC3: a real conversion (any provenance) → normal availability math, no gap treatment ─────

    [Fact]
    public async Task Existing_conversion_gets_normal_availability_no_gap()
    {
        var html = await GetCookPageAsync();
        var row = RowFor(html, "Milk");

        // 1000 ml converts to 4 cups ≥ 1 cup needed → in stock, no gap, no shortfall.
        Assert.NotNull(row.QuerySelector(".cook-ing-status--ok"));
        Assert.Null(row.QuerySelector(".cook-unitgap-tag"));
        Assert.Null(row.QuerySelector(".cook-shortfall-tag"));
    }

    // ── AC4: confirm cook still proceeds when a line is a unit gap (no consume-path change) ───────

    [Fact]
    public async Task Confirm_cook_still_proceeds_with_a_unit_gap()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, CookUnitGapFixture.HouseholdGuid.ToString());

        var url = $"/Recipes/{factory.RecipeId}/Cook";
        var pageHtml = await (await client.GetAsync($"{url}?Servings=1")).Content.ReadAsStringAsync();
        var match = System.Text.RegularExpressions.Regex.Match(
            pageHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token on the Cook page.");

        var response = await client.PostAsync(url, new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("__RequestVerificationToken", match.Groups[1].Value),
            new KeyValuePair<string, string>("Id", factory.RecipeId.ToString()),
            new KeyValuePair<string, string>("Servings", "1"),
        ]));

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after a proceed-with-unit-gap cook, got {(int)response.StatusCode}.");
    }
}

// ── Fixture + fakes ──────────────────────────────────────────────────────────────────────────────

/// <summary>Fixture data for the Cook unit-gap tests (plantry-qll2.5).</summary>
public static class CookUnitGapFixture
{
    public static readonly Guid HouseholdGuid = Guid.Parse("dddddddd-0000-0000-0000-000000000001");

    public static readonly RecipeId RecipeId = Plantry.Recipes.Domain.RecipeId.From(
        Guid.Parse("dddddddd-0000-0000-0000-000000000002"));

    public static readonly Guid CashewId = Guid.Parse("d1111111-1111-1111-1111-111111111111"); // unit gap
    public static readonly Guid FlourId  = Guid.Parse("d2222222-2222-2222-2222-222222222222"); // true zero
    public static readonly Guid MilkId   = Guid.Parse("d3333333-3333-3333-3333-333333333333"); // convertible

    public static readonly Guid GramUnitId = Guid.Parse("dddddddd-1111-0000-0000-000000000001");
    public static readonly Guid CupUnitId  = Guid.Parse("dddddddd-2222-0000-0000-000000000002");
    public static readonly Guid MlUnitId   = Guid.Parse("dddddddd-3333-0000-0000-000000000003");

    public static Recipe Build()
    {
        var hid = HouseholdId.From(HouseholdGuid);
        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
        var recipe = Recipe.Create(hid, "Unit Gap Bowl", defaultServings: 1, clock).Value;
        recipe.ReplaceIngredients(
        [
            new IngredientLine(CashewId, 1m,   CupUnitId,  GroupHeading: null, Ordinal: 1),
            new IngredientLine(FlourId,  200m, GramUnitId, GroupHeading: null, Ordinal: 2),
            new IngredientLine(MilkId,   1m,   CupUnitId,  GroupHeading: null, Ordinal: 3),
        ], clock);
        return recipe;
    }

    public static IReadOnlyDictionary<Guid, CatalogProduct> Products() =>
        new Dictionary<Guid, CatalogProduct>
        {
            [CashewId] = new(CashewId, "Cashews", TrackStock: true, GramUnitId, null, IsParent: false, []),
            [FlourId]  = new(FlourId,  "Flour",   TrackStock: true, GramUnitId, null, IsParent: false, []),
            [MilkId]   = new(MilkId,   "Milk",    TrackStock: true, MlUnitId,   null, IsParent: false, []),
        };

    public static IReadOnlyDictionary<Guid, string> UnitCodes() =>
        new Dictionary<Guid, string>
        {
            [GramUnitId] = "g",
            [CupUnitId]  = "cup",
            [MlUnitId]   = "ml",
        };

    /// <summary>Cashews 480 g (unit gap), Milk 1000 ml (convertible). Flour has no stock record (true zero).</summary>
    public static IReadOnlyDictionary<Guid, ProductStock> Stock() =>
        new Dictionary<Guid, ProductStock>
        {
            [CashewId] = new(CashewId, 480m,  GramUnitId, null),
            [MilkId]   = new(MilkId,   1000m, MlUnitId,   null),
        };
}

/// <summary>
/// Converter for the unit-gap tests: same-unit converts pass; Milk has a real ml↔cup path
/// (1000 ml → 4 cups, any provenance); everything else cross-unit fails (e.g. Cashews g↔cup).
/// </summary>
public sealed class FakeUnitGapConverter : IUnitConverter
{
    public Task<Result<decimal>> ConvertAsync(
        Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default)
    {
        if (fromUnitId == toUnitId)
            return Task.FromResult(Result<decimal>.Success(amount));
        if (productId == CookUnitGapFixture.MilkId)
            return Task.FromResult(Result<decimal>.Success(4m)); // 1000 ml → 4 cups
        return Task.FromResult(Result<decimal>.Failure(Error.Custom("Test.NoPath", "No conversion path.")));
    }
}

/// <summary>WAF for the Cook unit-gap tests — real Plantry.Web pipeline, in-memory seams.</summary>
public sealed class CookUnitGapFactory : WebApplicationFactory<Program>
{
    public Recipe Recipe { get; } = CookUnitGapFixture.Build();
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
                new FakeCookCatalogReader(CookUnitGapFixture.Products(), CookUnitGapFixture.UnitCodes()));

            services.RemoveAll<IInventoryStockReader>();
            services.AddSingleton<IInventoryStockReader>(
                new FakeCookStockReader(CookUnitGapFixture.Stock()));

            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeUnitGapConverter());
            services.AddFakeQuantityFormatter();

            services.RemoveAll<IInventoryConsumer>();
            services.AddSingleton<IInventoryConsumer>(new FakeCookInventoryConsumer());

            services.RemoveAll<ICookEventRepository>();
            services.AddSingleton<ICookEventRepository>(new FakeCookEventRepository());

            // CookRecipe's just-in-time yield-product resolution (plantry-iejb) requires ICatalogWriter —
            // not exercised by these unit-gap tests, but the WAF must still boot.
            services.RemoveAll<ICatalogWriter>();
            services.AddSingleton<ICatalogWriter>(new FakeCatalogWriter());

            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(new FakeTagRepository(new Dictionary<TagId, string>()));

            services.RemoveAll<IPriceReader>();
            services.AddSingleton<IPriceReader>(new FakeDetailPriceReader(new Dictionary<Guid, PricePoint>()));
        });
    }
}
