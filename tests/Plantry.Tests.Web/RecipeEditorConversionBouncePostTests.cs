using System.Net;
using System.Text.RegularExpressions;
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
/// plantry-dnbe: POST-bounce coverage for the conversion-prompt robustness guard obg3 established.
///
/// <para>obg3 (and its hhy2/tg79 splits) guarded the AJAX <c>OnGetCheckConversionAsync</c> handler so an
/// unresolvable stock/recipe unit returns explicit <c>defaultUnitMissing</c>/<c>recipeUnitMissing</c> flags
/// instead of a half-empty payload that rendered a blank unit sentence + an option-less dropdown. But the
/// AUTHORITATIVE POST-time path — <c>AuthorRecipe</c> re-validating every tracked line on every save and
/// bouncing to <see cref="AuthorRecipeResult.NeedsConversion"/> — never carried that guard. So editing an
/// existing line's quantity (not unit) and saving, on a product whose <c>DefaultUnitId</c> dangles (a DM-3
/// soft reference with no FK), re-rendered the row with <c>defaultUnitMissing:false</c> and a blank
/// <c>defaultUnitCode</c> — reproducing obg3's exact defect via the POST render. The reporting user hit this
/// live on their household's "Pasta Dry" ingredient.</para>
///
/// <para>The fix sets the same per-axis flags in the page model's <see cref="AuthorRecipeResult.NeedsConversion"/>
/// case (resolving each needed conversion axis against the household unit list), so the FIRST render after a
/// save bounce is already correct — not relying on the client's <c>maybeHydrateRowConversion</c> AJAX
/// rehydration to fix it after the fact. These tests drive a real POST bounce and assert the seeded Alpine
/// row state carries the correct flags.</para>
/// </summary>
public sealed class RecipeEditorConversionBouncePostTests
{
    private static HttpClient AuthenticatedClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, RecipeEditorFixture.HouseholdAId.ToString());
        return client;
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string editPath)
    {
        var html = await (await client.GetAsync(editPath)).Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, $"No antiforgery token found on {editPath}.");
        return match.Groups[1].Value;
    }

    /// <summary>
    /// Editing an existing line's quantity on a product whose default/stock unit is unresolvable must bounce
    /// to NeedsConversion AND seed <c>defaultUnitMissing:true</c> for that row on the very first render — so
    /// the landed-row conversion block shows the friendly "has no stock unit set" message instead of a blank
    /// unit sentence. The recipe-line unit ("ea") resolves fine, so <c>recipeUnitMissing</c> is present and
    /// false (obg3's stock copy takes precedence). The pre-fix always-false seed produced
    /// <c>defaultUnitMissing:false</c> with a blank <c>defaultUnitCode</c> — the exact regression this guards.
    /// </summary>
    [Fact]
    public async Task EditQty_On_DanglingDefaultUnit_Bounces_With_DefaultUnitMissing_True()
    {
        using var factory = new BounceFactory();
        var client = AuthenticatedClient(factory);
        var editPath = $"/Recipes/{RecipeEditorFixture.DanglingDefaultRecipeId.Value}/Edit";
        var token = await GetAntiforgeryTokenAsync(client, editPath);

        var form = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.Name",            "Pantry Oil Dish"),
            new("Input.DefaultServings", "2"),
            new("Input.Lines[0].Ordinal",   "0"),
            new("Input.Lines[0].ProductId", RecipeEditorFixture.DanglingDefaultId.ToString()),
            new("Input.Lines[0].Quantity",  "1"), // edited from the stored 4 — the dogfooded repro
            new("Input.Lines[0].UnitId",    RecipeEditorFixture.EachUnitId.ToString()),
            // No conversion factor supplied — the converter cannot bridge ea → the dangling default.
        };

        var response = await client.PostAsync(editPath, new FormUrlEncodedContent(form));

        // Blocked → re-rendered form (200), not a redirect.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        // The Alpine row state is emitted as JSON inside an HTML-encoded x-data attribute — decode before
        // asserting on the serialised booleans.
        var decoded = WebUtility.HtmlDecode(html);

        // The fix: the stock axis (product default = dangling unit) is flagged missing on the first render.
        Assert.Contains("\"defaultUnitMissing\":true", decoded);
        // The recipe-line unit resolves, so its flag is present and false (stock copy wins).
        Assert.Contains("\"recipeUnitMissing\":false", decoded);
        // Regression guard: the pre-fix always-false seed would emit defaultUnitMissing:false. With a single
        // ingredient row and inclusion rows carrying no such property, the false form must not appear at all.
        Assert.DoesNotContain("\"defaultUnitMissing\":false", decoded);
    }

    // ── Factory + fakes ────────────────────────────────────────────────────────────

    /// <summary>
    /// WAF seeded with the dangling-default recipe, a converter that never finds a path (every cross-unit
    /// line is a gap), and assistive-AI disabled (so a gap BLOCKS with NeedsConversion rather than deferring).
    /// </summary>
    private sealed class BounceFactory : WebApplicationFactory<Program>
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
                services.AddSingleton<IRecipeRepository>(new FakeEditorRecipeRepository(
                    new ConstantTenantContext(RecipeEditorFixture.HouseholdAId),
                    RecipeEditorFixture.BuildDanglingDefaultLine()));

                services.RemoveAll<ITagRepository>();
                services.AddSingleton<ITagRepository>(new FakeTagRepository(
                    RecipeEditorFixture.TagNames(), RecipeEditorFixture.ActiveTags()));

                services.RemoveAll<ICatalogProductReader>();
                services.AddSingleton<ICatalogProductReader>(new FakeEditorProductReader(
                    RecipeEditorFixture.ProductSummaries(),
                    RecipeEditorFixture.UnitCodes(),
                    RecipeEditorFixture.UnitOptions(),
                    RecipeEditorFixture.ProductDefaultUnits()));

                services.RemoveAll<ICatalogWriter>();
                services.AddSingleton<ICatalogWriter>(new FakeCatalogWriter());

                // Converter that never finds a path → every cross-unit line is a gap.
                services.RemoveAll<IUnitConverter>();
                services.AddSingleton<IUnitConverter>(new NoPathUnitConverter());

                // Assistive AI off → the gap BLOCKS with the inline C10 prompt (deferral requires the toggle on).
                services.RemoveAll<IAiAssistanceGateReader>();
                services.AddSingleton<IAiAssistanceGateReader>(new DisabledGateReader());
            });
        }
    }

    private sealed class NoPathUnitConverter : IUnitConverter
    {
        public Task<Result<decimal>> ConvertAsync(
            Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default) =>
            fromUnitId == toUnitId
                ? Task.FromResult(Result<decimal>.Success(amount))
                : Task.FromResult(Result<decimal>.Failure(
                    Error.Custom("Catalog.UnresolvableConversion", "No conversion path.")));
    }

    private sealed class DisabledGateReader : IAiAssistanceGateReader
    {
        public Task<bool> IsEnabledAsync(CancellationToken ct = default) => Task.FromResult(false);
    }
}
