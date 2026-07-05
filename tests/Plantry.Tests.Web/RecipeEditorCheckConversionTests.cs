using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// Integration tests for <c>OnGetCheckConversionAsync</c> — the lightweight GET handler that tells
/// the recipe editor whether a cross-dimension conversion gap exists for a chosen product + unit pair.
///
/// <para>The handler is the server-side half of the live C10 early-UX feature (plantry-c9s2):
/// when the author picks a unit in the add/edit sheet, a debounced Alpine <c>$watch</c> calls this
/// endpoint; the JSON response drives whether the in-sheet conversion prompt is shown.</para>
///
/// <para>Three paths are tested per the issue's "Tests" section:
/// <list type="number">
///   <item>Same-dimension pick (e.g. g → g) — converter succeeds — <c>needsConversion:false</c>.</item>
///   <item>Cross-dimension pick with no existing path — converter fails — <c>needsConversion:true</c>
///         with <c>defaultUnitId</c> and <c>defaultUnitCode</c>.</item>
///   <item>Pick where the converter succeeds even for a different unit (path exists via ProductConversion)
///         — <c>needsConversion:false</c>.</item>
/// </list>
/// </para>
/// </summary>
public sealed class RecipeEditorCheckConversionTests : IDisposable
{
    private readonly ConversionCheckFactory _sameDimensionFactory = new(alwaysSucceeds: true);
    private readonly ConversionCheckFactory _crossDimensionFactory = new(alwaysSucceeds: false);

    public void Dispose()
    {
        _sameDimensionFactory.Dispose();
        _crossDimensionFactory.Dispose();
    }

    private static HttpClient AuthenticatedClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeEditorFixture.HouseholdAId.ToString());
        return client;
    }

    // ── Same-dimension (or path-exists) → needsConversion:false ─────────────────

    /// <summary>
    /// When the unit converter succeeds (same-dimension unit or an existing ProductConversion path),
    /// the endpoint returns <c>{"needsConversion":false}</c> and the Alpine watch must NOT show the
    /// in-sheet prompt.
    /// </summary>
    [Fact]
    public async Task CheckConversion_same_dimension_returns_needsConversion_false()
    {
        var client = AuthenticatedClient(_sameDimensionFactory);
        var productId = RecipeEditorFixture.PastaId;
        var unitId    = RecipeEditorFixture.GramUnitId; // same as default unit for Pasta

        var response = await client.GetAsync(
            $"/Recipes/New?handler=CheckConversion&productId={productId}&fromUnitId={unitId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("needsConversion", out var nc));
        Assert.False(nc.GetBoolean(), $"Expected needsConversion:false for same-dimension unit. Got: {json}");
    }

    /// <summary>
    /// Same-unit shortcut: when <c>fromUnitId == product.DefaultUnitId</c> the handler short-circuits
    /// without calling the converter and returns <c>needsConversion:false</c>.
    /// </summary>
    [Fact]
    public async Task CheckConversion_same_unit_as_default_returns_needsConversion_false()
    {
        // Use cross-dimension factory (converter always fails) — the shortcut path bypasses the converter.
        var client = AuthenticatedClient(_crossDimensionFactory);
        var productId = RecipeEditorFixture.PastaId;
        var defaultUnitId = RecipeEditorFixture.GramUnitId; // GramUnitId IS the default for Pasta

        var response = await client.GetAsync(
            $"/Recipes/New?handler=CheckConversion&productId={productId}&fromUnitId={defaultUnitId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("needsConversion").GetBoolean(),
            $"Same unit as default should never need conversion. Got: {json}");
    }

    // ── Cross-dimension, no path → needsConversion:true ─────────────────────────

    /// <summary>
    /// When the unit converter reports no path (cross-dimension gap), the endpoint returns
    /// <c>{"needsConversion":true,"defaultUnitId":"...","defaultUnitCode":"g"}</c>.
    /// The client uses <c>defaultUnitCode</c> to render the conversion prompt label.
    /// </summary>
    [Fact]
    public async Task CheckConversion_cross_dimension_no_path_returns_needsConversion_true_with_default_unit()
    {
        var client = AuthenticatedClient(_crossDimensionFactory);
        var productId    = RecipeEditorFixture.PastaId;
        var fromUnitId   = RecipeEditorFixture.EachUnitId; // ea ≠ g → cross-dimension gap (converter always fails)

        var response = await client.GetAsync(
            $"/Recipes/New?handler=CheckConversion&productId={productId}&fromUnitId={fromUnitId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("needsConversion").GetBoolean(),
            $"Expected needsConversion:true for cross-dimension gap. Got: {json}");

        // defaultUnitId must be Pasta's default (GramUnitId).
        Assert.True(root.TryGetProperty("defaultUnitId", out var duid));
        Assert.Equal(
            RecipeEditorFixture.GramUnitId.ToString(),
            duid.GetString());

        // defaultUnitCode must be "g" (the code for GramUnitId in the fixture unit list).
        Assert.True(root.TryGetProperty("defaultUnitCode", out var duc));
        Assert.Equal("g", duc.GetString());
    }

    /// <summary>
    /// plantry-qno9: on a cross-dimension gap the handler also returns the two axis-locked unit lists
    /// that drive the four-field equation editor — <c>stockUnits</c> (units sharing the product default
    /// unit's dimension) and <c>recipeUnits</c> (units sharing the chosen recipe-line unit's dimension) —
    /// each carrying <c>id</c>, <c>code</c>, and <c>factorToBase</c>. In the fixture gram is mass and each
    /// is count, so the lists are disjoint: stock = [g], recipe = [ea], and neither leaks across axes.
    /// </summary>
    [Fact]
    public async Task CheckConversion_cross_dimension_returns_axis_locked_unit_lists()
    {
        var client = AuthenticatedClient(_crossDimensionFactory);
        var productId  = RecipeEditorFixture.PastaId;      // default unit = g (mass)
        var fromUnitId = RecipeEditorFixture.EachUnitId;   // ea (count) → cross-dimension gap

        var response = await client.GetAsync(
            $"/Recipes/New?handler=CheckConversion&productId={productId}&fromUnitId={fromUnitId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("needsConversion").GetBoolean(), json);

        // LEFT axis: only the product stock dimension (mass) — g, and NOT ea.
        var stockUnits = root.GetProperty("stockUnits").EnumerateArray().ToList();
        var stockCodes = stockUnits.Select(u => u.GetProperty("code").GetString()).ToList();
        Assert.Contains("g", stockCodes);
        Assert.DoesNotContain("ea", stockCodes);
        // Each entry carries the metadata the client echo line needs.
        Assert.All(stockUnits, u =>
        {
            Assert.True(u.TryGetProperty("id", out _));
            Assert.True(u.TryGetProperty("factorToBase", out _));
        });

        // RIGHT axis: only the recipe-line dimension (count) — ea, and NOT g.
        var recipeCodes = root.GetProperty("recipeUnits").EnumerateArray()
            .Select(u => u.GetProperty("code").GetString()).ToList();
        Assert.Contains("ea", recipeCodes);
        Assert.DoesNotContain("g", recipeCodes);
    }

    /// <summary>
    /// Unknown product id returns <c>{"needsConversion":false}</c> — the handler treats a missing
    /// product as a no-op rather than an error, because the client validation and server-side R7
    /// backstop will catch it.
    /// </summary>
    [Fact]
    public async Task CheckConversion_unknown_product_returns_needsConversion_false()
    {
        var client     = AuthenticatedClient(_sameDimensionFactory);
        var unknownId  = Guid.NewGuid();
        var unitId     = RecipeEditorFixture.GramUnitId;

        var response = await client.GetAsync(
            $"/Recipes/New?handler=CheckConversion&productId={unknownId}&fromUnitId={unitId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("needsConversion").GetBoolean(),
            $"Unknown product should return needsConversion:false. Got: {json}");
    }
}

// ── Factory ──────────────────────────────────────────────────────────────────────────

/// <summary>
/// WAF for the check-conversion endpoint tests. Configures the product reader to return the
/// fixture products and a unit converter that either always succeeds (<paramref name="alwaysSucceeds"/>
/// <c>true</c>, simulating a same-dimension path) or always fails (<c>false</c>, simulating a
/// cross-dimension gap with no existing ProductConversion).
/// </summary>
internal sealed class ConversionCheckFactory(bool alwaysSucceeds) : WebApplicationFactory<Program>
{
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
                new FakeEditorRecipeRepository(sp.GetRequiredService<Plantry.SharedKernel.Tenancy.ITenantContext>()));

            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(
                new FakeTagRepository(RecipeEditorFixture.TagNames(), RecipeEditorFixture.ActiveTags()));

            // Product reader: returns fixture products with default unit ids so the handler can
            // resolve which unit is the product default and compare against fromUnitId.
            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeEditorProductReader(
                    RecipeEditorFixture.ProductSummaries(),
                    RecipeEditorFixture.UnitCodes(),
                    RecipeEditorFixture.UnitOptions(),
                    RecipeEditorFixture.ProductDefaultUnits()));

            services.RemoveAll<ICatalogWriter>();
            services.AddSingleton<ICatalogWriter>(new FakeCatalogWriter());

            // Unit converter: configurable to succeed (same-dimension path) or fail (cross-dimension gap).
            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(
                alwaysSucceeds
                    ? (IUnitConverter)new FakeUnitConverter()
                    : new AlwaysFailUnitConverter());
        });
    }
}

/// <summary>
/// <see cref="IUnitConverter"/> that always reports "no path" — simulates a cross-dimension gap
/// (e.g. "cups" → "g" for a product with no ProductConversion on file).
/// </summary>
internal sealed class AlwaysFailUnitConverter : IUnitConverter
{
    public Task<Result<decimal>> ConvertAsync(
        Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default) =>
        Task.FromResult(Result<decimal>.Failure(
            Error.Custom("Conversion.NoPath", "No conversion path exists between these units.")));
}
