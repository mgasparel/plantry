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
/// L4 tests for the substitute-aware C11 picker (plantry-aqpa.3) — the Cook page offers a declared
/// one-hop substitution edge alongside the direct product, and posting that choice deducts the
/// ratio-converted amount from the SUBSTITUTE's own stock, not the recipe's named product.
///
/// All scenarios hang off the fixture recipe's Pasta line (leaf, 400g needed at scale=1):
/// <list type="bullet">
///   <item>Fusilli — identity-unit edge ("100g Fusilli ≡ 200g Pasta"), every hop is fromUnit==toUnit.</item>
///   <item>Orzo — Hop C is a REAL non-identity conversion (declared in grams, Orzo's own stock is tbsp).</item>
///   <item>Barley — Hop A has no conversion path (edge target unit ≠ the line unit, unregistered pair) →
///     the WHOLE edge is disqualified, Barley never appears.</item>
///   <item>Quinoa — Hop C has no conversion path (Quinoa's own stock unit is unregistered for the edge's
///     substitute unit) → this one candidate is disqualified, Quinoa never appears.</item>
///   <item>GrainMedley — a PARENT substitute; DM-19 rollup offers each live variant child as its own
///     candidate, factor 1.0 (not a second substitution hop).</item>
/// </list>
/// </summary>
public sealed class CookSubstitutionPickerTests : IDisposable
{
    private readonly CookSubstitutionFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private const int PostedServings = 4; // fixture recipe default — scale = 1

    private HttpClient AuthenticatedClient() =>
        _factory.CreateClient(new() { AllowAutoRedirect = false }).With(c =>
            c.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, CookConfirmFixture.HouseholdAId.ToString()));

    private string CookUrl => $"/Recipes/{_factory.RecipeId}/Cook";

    private async Task<(string Html, HttpResponseMessage Response)> GetCookPageAsync(HttpClient client)
    {
        var response = await client.GetAsync($"{CookUrl}?Servings={PostedServings}");
        var html = await response.Content.ReadAsStringAsync();
        return (html, response);
    }

    private async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var (html, _) = await GetCookPageAsync(client);
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the Cook page.");
        return match.Groups[1].Value;
    }

    /// <summary>
    /// Isolates one line's object out of the Alpine seed payload (<c>cookConfirm(linesJson, unitsJson)</c>,
    /// HTML-attribute-encoded on <c>#cook-confirm</c>'s <c>x-data</c>) by its <c>"id":"&lt;lineId&gt;"</c>
    /// marker. A line object here is flat (scalars + at most one flat string array, no nested braces), so
    /// bracket-counting isn't needed — the first <c>{</c> before, and first <c>}</c> after, the marker
    /// bound the object exactly.
    /// </summary>
    private static string ExtractLineJson(string html, string lineId)
    {
        var decoded = System.Net.WebUtility.HtmlDecode(html);
        var marker = $"\"id\":\"{lineId}\"";
        var idx = decoded.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(idx >= 0, $"Line '{lineId}' not found in the seeded Alpine payload.");
        var start = decoded.LastIndexOf('{', idx);
        var end = decoded.IndexOf('}', idx);
        return decoded[start..(end + 1)];
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

    // ── GET: identity-unit substitute (Fusilli) ────────────────────────────────────────────────────

    [Fact]
    public async Task Get_renders_substitute_option_with_available_and_deduct_amounts()
    {
        var client = AuthenticatedClient();
        var (html, response) = await GetCookPageAsync(client);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The substitute's name and "or substitute" label must render on the Pasta line.
        Assert.Contains("Or substitute", html, StringComparison.Ordinal);
        Assert.Contains("Fusilli", html, StringComparison.Ordinal);

        // The substitute's OWN available stock (1000g) and the ratio-converted deduction (200g) both
        // render — distinct amounts, so the user sees "have 1000g" and "will use ~200g" separately.
        Assert.Contains("1000", html, StringComparison.Ordinal);
        Assert.Contains("200", html, StringComparison.Ordinal);

        // The direct product itself is STILL offered as an option (never JUST the substitutes) —
        // Pasta's own name and on-hand amount (600g, from CookConfirmFixture.Stock()) render too.
        Assert.Contains("Rigatoni", html, StringComparison.Ordinal); // Pasta's fixture display name
        Assert.Contains("600", html, StringComparison.Ordinal);

        // The hidden-input contract still posts a single choice per line — no splitting UI.
        Assert.Contains($"PickerSelections[{_factory.PastaIngredientId}]", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_direct_stock_stays_default_selection_not_substitute()
    {
        var client = AuthenticatedClient();
        var (html, _) = await GetCookPageAsync(client);

        // The default fallback (`pickerVariant(key) ?? defaultVariant`) must resolve to Pasta's OWN
        // product id, not Fusilli's — direct stock stays the default/preselected choice.
        Assert.Contains(
            $"pickerVariant('{_factory.PastaIngredientId}') ?? '{CookConfirmFixture.PastaId}'",
            html, StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"pickerVariant('{_factory.PastaIngredientId}') ?? '{CookSubstitutionFixtureData.FusilliId}'",
            html, StringComparison.Ordinal);
    }

    // ── GET: a broken conversion path disqualifies the option, never the page ─────────────────────

    [Fact]
    public async Task Get_edge_with_broken_target_unit_conversion_omits_the_whole_edge()
    {
        var client = AuthenticatedClient();
        var (html, response) = await GetCookPageAsync(client);

        // Barley's edge declares a target unit (EachUnitId) with no registered conversion from the
        // line's own unit (GramUnitId) — Hop A fails, so the ENTIRE edge (Barley's only candidate) must
        // be disqualified from the picker. The page must still render successfully (200), never error.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Barley", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_candidate_with_broken_own_unit_conversion_is_omitted()
    {
        var client = AuthenticatedClient();
        var (html, response) = await GetCookPageAsync(client);

        // Quinoa's edge is declared entirely in grams (Hop A/B succeed) but Quinoa's OWN stock unit
        // (EachUnitId) has no registered conversion from grams — Hop C fails, disqualifying this ONE
        // candidate. The page must still render successfully.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Quinoa", html, StringComparison.Ordinal);
    }

    // ── GET: a genuine non-identity conversion composes correctly (Orzo, Hop C) ───────────────────

    [Fact]
    public async Task Get_renders_substitute_deduction_via_real_non_identity_conversion()
    {
        var client = AuthenticatedClient();
        var (html, response) = await GetCookPageAsync(client);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 400g Pasta * (100g Orzo-declared / 200g Pasta) = 200g, landed via a REAL 10g-per-tbsp
        // conversion into Orzo's own stock unit (tbsp) = 20 tbsp. This is not an identity hop — it
        // exercises unitConverter.ConvertAsync with genuinely different from/to units.
        Assert.Contains("Orzo", html, StringComparison.Ordinal);
        Assert.Contains("20", html, StringComparison.Ordinal);
    }

    // ── GET: a substitute that is itself a parent offers each live variant child (DM-19 rollup) ───

    [Fact]
    public async Task Get_parent_substitute_offers_each_variant_child_as_its_own_candidate()
    {
        var client = AuthenticatedClient();
        var (html, response) = await GetCookPageAsync(client);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Both live variant children of the parent substitute are offered individually — factor 1.0
        // rollup (not a second substitution hop) — each with its own name.
        Assert.Contains("GrainMedley Bag A", html, StringComparison.Ordinal);
        Assert.Contains("GrainMedley Bag B", html, StringComparison.Ordinal);
        // The parent substitute product itself must NOT be offered as a directly-selectable id.
        Assert.DoesNotContain(CookSubstitutionFixtureData.GrainMedleyParentId.ToString(), html, StringComparison.Ordinal);
    }

    // ── POST: choosing the substitute deducts the converted amount from ITS OWN stock ─────────────

    [Fact]
    public async Task Post_picking_substitute_consumes_converted_quantity_from_substitute_stock()
    {
        var client = AuthenticatedClient();

        var response = await PostCookAsync(client,
        [
            new("PickerSelections[0].IngredientId", _factory.PastaIngredientId.ToString()),
            new("PickerSelections[0].VariantId",    CookSubstitutionFixtureData.FusilliId.ToString()),
        ]);

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after successful cook, got {(int)response.StatusCode}.");

        var calls = _factory.Consumer.Calls;

        // The consume must target Fusilli — never the recipe's own Pasta product (truthful stock journal).
        Assert.DoesNotContain(calls, c => c.ProductId == CookConfirmFixture.PastaId);
        var fusilliCall = Assert.Single(calls, c => c.ProductId == CookSubstitutionFixtureData.FusilliId);

        // 400g Pasta required * (100g Fusilli / 200g Pasta) = 200g Fusilli — NOT 400g (the recipe's raw
        // quantity) and NOT the Pasta unit misapplied to Fusilli's stock.
        Assert.Equal(200m, fusilliCall.Quantity);
        Assert.Equal(CookConfirmFixture.GramUnitId, fusilliCall.UnitId);
    }

    [Fact]
    public async Task Post_picking_substitute_via_real_non_identity_conversion_deducts_converted_unit()
    {
        var client = AuthenticatedClient();

        var response = await PostCookAsync(client,
        [
            new("PickerSelections[0].IngredientId", _factory.PastaIngredientId.ToString()),
            new("PickerSelections[0].VariantId",    CookSubstitutionFixtureData.OrzoId.ToString()),
        ]);

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after successful cook, got {(int)response.StatusCode}.");

        var orzoCall = Assert.Single(_factory.Consumer.Calls, c => c.ProductId == CookSubstitutionFixtureData.OrzoId);

        // 200g landed via the real 10g-per-tbsp conversion → 20 tbsp, in Orzo's OWN stock unit.
        Assert.Equal(20m, orzoCall.Quantity);
        Assert.Equal(CookConfirmFixture.TbspUnitId, orzoCall.UnitId);
    }

    [Fact]
    public async Task Post_picking_variant_child_of_a_parent_substitute_deducts_from_that_child()
    {
        var client = AuthenticatedClient();

        var response = await PostCookAsync(client,
        [
            new("PickerSelections[0].IngredientId", _factory.PastaIngredientId.ToString()),
            new("PickerSelections[0].VariantId",    CookSubstitutionFixtureData.GrainMedleyChildAId.ToString()),
        ]);

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after successful cook, got {(int)response.StatusCode}.");

        var calls = _factory.Consumer.Calls;
        Assert.DoesNotContain(calls, c => c.ProductId == CookSubstitutionFixtureData.GrainMedleyParentId);
        Assert.DoesNotContain(calls, c => c.ProductId == CookSubstitutionFixtureData.GrainMedleyChildBId);
        var childCall = Assert.Single(calls, c => c.ProductId == CookSubstitutionFixtureData.GrainMedleyChildAId);
        Assert.Equal(200m, childCall.Quantity);
        Assert.Equal(CookConfirmFixture.GramUnitId, childCall.UnitId);
    }

    [Fact]
    public async Task Post_direct_selection_still_consumes_recipe_product_unaffected_by_edge()
    {
        var client = AuthenticatedClient();

        // No picker selection posted for Pasta at all — default auto-selection (C7), exactly as before
        // plantry-aqpa.3: a declared substitution edge existing must never change the outcome when the
        // user picks (or defaults to) the direct product.
        var response = await PostCookAsync(client, []);

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after successful cook, got {(int)response.StatusCode}.");

        var calls = _factory.Consumer.Calls;
        var pastaCall = Assert.Single(calls, c => c.ProductId == CookConfirmFixture.PastaId);
        Assert.Equal(400m, pastaCall.Quantity);
        Assert.Equal(CookConfirmFixture.GramUnitId, pastaCall.UnitId);
        Assert.DoesNotContain(calls, c => c.ProductId == CookSubstitutionFixtureData.FusilliId);
    }

    // ── GET: Alpine seed payload carries the preselected direct product + substitute ids ───────────

    [Fact]
    public async Task Get_seeds_direct_variant_id_and_substitute_ids_in_alpine_payload()
    {
        var client = AuthenticatedClient();
        var (html, _) = await GetCookPageAsync(client);

        var lineJson = ExtractLineJson(html, _factory.PastaIngredientId.ToString());

        // arbiter ruling 5: the leaf-with-substitutes line's synthetic direct option is now seeded as
        // the preselected variantId (the IsParent conjunct was dropped from Cook.cshtml's seed).
        Assert.Contains($"\"variantId\":\"{CookConfirmFixture.PastaId}\"", lineJson, StringComparison.Ordinal);

        // arbiter ruling 3: every still-qualified substitute candidate id is seeded so the client can
        // recognise a substitute-selected line and suppress Use-it-up. Disqualified candidates (Barley,
        // Quinoa) must NOT appear.
        Assert.Contains("\"substituteIds\":[", lineJson, StringComparison.Ordinal);
        Assert.Contains(CookSubstitutionFixtureData.FusilliId.ToString(), lineJson, StringComparison.Ordinal);
        Assert.Contains(CookSubstitutionFixtureData.OrzoId.ToString(), lineJson, StringComparison.Ordinal);
        Assert.Contains(CookSubstitutionFixtureData.GrainMedleyChildAId.ToString(), lineJson, StringComparison.Ordinal);
        Assert.Contains(CookSubstitutionFixtureData.GrainMedleyChildBId.ToString(), lineJson, StringComparison.Ordinal);
        Assert.DoesNotContain(CookSubstitutionFixtureData.BarleyId.ToString(), lineJson, StringComparison.Ordinal);
        Assert.DoesNotContain(CookSubstitutionFixtureData.QuinoaId.ToString(), lineJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_line_with_no_substitutes_seeds_null_substitute_ids()
    {
        var client = AuthenticatedClient();
        var (html, _) = await GetCookPageAsync(client);

        var tomatoIngredientId = _factory.Recipe.Ingredients
            .Single(i => i.ProductId == CookConfirmFixture.TomatoId).Id.Value;
        var lineJson = ExtractLineJson(html, tomatoIngredientId.ToString());

        // Byte-for-byte unchanged: a line with no declared substitution edge seeds a null substituteIds
        // entry, exactly like every pre-aqpa.3 line.
        Assert.Contains("\"substituteIds\":null", lineJson, StringComparison.Ordinal);
    }

    // ── GET: the deduction hint is hidden once the quantity is overridden (arbiter ruling 2) ──────

    [Fact]
    public async Task Get_deduct_hint_is_bound_to_hide_when_quantity_modified()
    {
        var client = AuthenticatedClient();
        var (html, _) = await GetCookPageAsync(client);

        // ADR-020 §7 forbids re-deriving the three-hop conversion client-side, so the compliant fix is
        // to hide the GET-time figure once the quantity is modified (isModified is already the Cook
        // page's existing "has this line been overridden" predicate — see the plain-quantity display
        // span using the same binding).
        Assert.Contains(
            $"cook-picker__deduct\" x-show=\"!isModified('{_factory.PastaIngredientId}')\"",
            html, StringComparison.Ordinal);
    }

    // ── GET/POST: a parent substitute TARGET with zero live variant children (arbiter ruling 6) ───

    [Fact]
    public async Task Get_zero_variant_parent_with_substitute_offers_direct_option_alongside_substitute()
    {
        var client = AuthenticatedClient();
        var (html, response) = await GetCookPageAsync(client);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Garlic (zeroed to no live variants in this fixture) must still offer its OWN direct product —
        // never substitutes alone — alongside the declared substitute, Garlic Powder.
        Assert.Contains("Garlic Powder", html, StringComparison.Ordinal);
        var garlicLineJson = ExtractLineJson(html, _factory.GarlicIngredientId.ToString());
        Assert.Contains($"\"variantId\":\"{CookConfirmFixture.GarlicParentId}\"", garlicLineJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_picking_substitute_on_zero_variant_parent_consumes_substitute_not_parent()
    {
        var client = AuthenticatedClient();

        var response = await PostCookAsync(client,
        [
            new("PickerSelections[0].IngredientId", _factory.GarlicIngredientId.ToString()),
            new("PickerSelections[0].VariantId",    CookSubstitutionFixtureData.GarlicPowderId.ToString()),
        ]);

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after successful cook, got {(int)response.StatusCode}.");

        var calls = _factory.Consumer.Calls;
        Assert.DoesNotContain(calls, c => c.ProductId == CookConfirmFixture.GarlicParentId);
        var powderCall = Assert.Single(calls, c => c.ProductId == CookSubstitutionFixtureData.GarlicPowderId);
        // 3 ea Garlic required (scale=1) * (1 ea Powder / 1 ea Garlic) = 3 ea Garlic Powder.
        Assert.Equal(3m, powderCall.Quantity);
        Assert.Equal(CookConfirmFixture.EachUnitId, powderCall.UnitId);
    }

    // ── POST: a candidate that vanished between GET and POST must NEVER misdeduct at raw quantity ──

    [Fact]
    public async Task Post_picking_a_product_that_is_neither_direct_variant_nor_resolvable_substitute_falls_back_to_direct_product()
    {
        var client = AuthenticatedClient();

        // Quinoa is disqualified server-side (Hop C has no conversion path — see the GET test above), so
        // it never appears as a real picker option, yet nothing stops a malicious or stale client from
        // posting its id anyway. This must NEVER fall through to "deduct Pasta's raw 400g quantity, in
        // Pasta's unit, from Quinoa's stock" — the untruthful stock-journal entry the ticket forbids.
        // It must fall back to the direct product exactly as if no picker entry were posted.
        var response = await PostCookAsync(client,
        [
            new("PickerSelections[0].IngredientId", _factory.PastaIngredientId.ToString()),
            new("PickerSelections[0].VariantId",    CookSubstitutionFixtureData.QuinoaId.ToString()),
        ]);

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after successful cook, got {(int)response.StatusCode}.");

        var calls = _factory.Consumer.Calls;
        Assert.DoesNotContain(calls, c => c.ProductId == CookSubstitutionFixtureData.QuinoaId);
        var pastaCall = Assert.Single(calls, c => c.ProductId == CookConfirmFixture.PastaId);
        Assert.Equal(400m, pastaCall.Quantity);
        Assert.Equal(CookConfirmFixture.GramUnitId, pastaCall.UnitId);
    }
}

/// <summary>Fixture data for <see cref="CookSubstitutionPickerTests"/> — several substitute products +
/// edges layered on top of <see cref="CookConfirmFixture"/>'s base recipe/catalog/stock, each exercising
/// a distinct branch of <c>CookModel.ComputeSubstituteCandidatesAsync</c>.</summary>
internal static class CookSubstitutionFixtureData
{
    // Identity-unit substitute (every hop is fromUnit == toUnit).
    public static readonly Guid FusilliId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    public static readonly Guid FusilliEdgeId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    // Hop C is a REAL non-identity conversion (declared grams → Orzo's own stock unit, tbsp).
    public static readonly Guid OrzoId = Guid.Parse("aaaaaaaa-0000-1111-0000-000000000001");
    public static readonly Guid OrzoEdgeId = Guid.Parse("aaaaaaaa-0000-1111-0000-000000000002");

    // Hop A has no conversion path — the WHOLE edge is disqualified.
    public static readonly Guid BarleyId = Guid.Parse("aaaaaaaa-0000-1111-0000-000000000003");
    public static readonly Guid BarleyEdgeId = Guid.Parse("aaaaaaaa-0000-1111-0000-000000000004");

    // Hop C has no conversion path — this ONE candidate is disqualified.
    public static readonly Guid QuinoaId = Guid.Parse("aaaaaaaa-0000-1111-0000-000000000005");
    public static readonly Guid QuinoaEdgeId = Guid.Parse("aaaaaaaa-0000-1111-0000-000000000006");

    // A PARENT substitute — DM-19 rollup offers each live variant child as its own candidate.
    public static readonly Guid GrainMedleyParentId = Guid.Parse("aaaaaaaa-0000-1111-0000-000000000007");
    public static readonly Guid GrainMedleyChildAId = Guid.Parse("aaaaaaaa-0000-1111-0000-000000000008");
    public static readonly Guid GrainMedleyChildBId = Guid.Parse("aaaaaaaa-0000-1111-0000-000000000009");
    public static readonly Guid GrainMedleyEdgeId = Guid.Parse("aaaaaaaa-0000-1111-0000-00000000000a");

    // A substitute TARGETING a parent product with ZERO live variant children (arbiter ruling 6).
    public static readonly Guid GarlicPowderId = Guid.Parse("aaaaaaaa-0000-1111-0000-00000000000b");
    public static readonly Guid GarlicPowderEdgeId = Guid.Parse("aaaaaaaa-0000-1111-0000-00000000000c");

    public static IReadOnlyDictionary<Guid, CatalogProduct> Products()
    {
        var map = new Dictionary<Guid, CatalogProduct>(CookConfirmFixture.Products())
        {
            [FusilliId] = new(FusilliId, "Fusilli", TrackStock: true, CookConfirmFixture.GramUnitId, null,
                IsParent: false, []),
            [OrzoId] = new(OrzoId, "Orzo", TrackStock: true, CookConfirmFixture.TbspUnitId, null,
                IsParent: false, []),
            [BarleyId] = new(BarleyId, "Barley", TrackStock: true, CookConfirmFixture.GramUnitId, null,
                IsParent: false, []),
            [QuinoaId] = new(QuinoaId, "Quinoa", TrackStock: true, CookConfirmFixture.EachUnitId, null,
                IsParent: false, []),
            [GrainMedleyParentId] = new(GrainMedleyParentId, "GrainMedley", TrackStock: true,
                CookConfirmFixture.GramUnitId, null, IsParent: true, [GrainMedleyChildAId, GrainMedleyChildBId]),
            [GrainMedleyChildAId] = new(GrainMedleyChildAId, "GrainMedley Bag A", TrackStock: true,
                CookConfirmFixture.GramUnitId, GrainMedleyParentId, IsParent: false, []),
            [GrainMedleyChildBId] = new(GrainMedleyChildBId, "GrainMedley Bag B", TrackStock: true,
                CookConfirmFixture.GramUnitId, GrainMedleyParentId, IsParent: false, []),
            [GarlicPowderId] = new(GarlicPowderId, "Garlic Powder", TrackStock: true,
                CookConfirmFixture.EachUnitId, null, IsParent: false, []),
            // Overrides the base fixture's Garlic (2 live variants) to ZERO variants — the degenerate
            // catalog shape arbiter ruling 6 covers. Scoped to THIS factory only (a fresh WAF instance
            // per test), so no other test file's Garlic-variant-picker assertions are affected.
            [CookConfirmFixture.GarlicParentId] = new(CookConfirmFixture.GarlicParentId, "Garlic",
                TrackStock: true, CookConfirmFixture.EachUnitId, null, IsParent: true, []),
        };
        return map;
    }

    public static IReadOnlyDictionary<Guid, Plantry.Recipes.Application.ProductStock> Stock()
    {
        var map = new Dictionary<Guid, Plantry.Recipes.Application.ProductStock>(CookConfirmFixture.Stock())
        {
            [FusilliId] = new(FusilliId, 1000m, CookConfirmFixture.GramUnitId, null),
            [OrzoId] = new(OrzoId, 50m, CookConfirmFixture.TbspUnitId, null),
            [BarleyId] = new(BarleyId, 500m, CookConfirmFixture.GramUnitId, null),
            [QuinoaId] = new(QuinoaId, 10m, CookConfirmFixture.EachUnitId, null),
            [GrainMedleyChildAId] = new(GrainMedleyChildAId, 300m, CookConfirmFixture.GramUnitId, null),
            [GrainMedleyChildBId] = new(GrainMedleyChildBId, 150m, CookConfirmFixture.GramUnitId, null),
            [GarlicPowderId] = new(GarlicPowderId, 20m, CookConfirmFixture.EachUnitId, null),
        };
        return map;
    }

    public static IReadOnlyList<SubstitutionEdge> Edges() =>
    [
        // "100g Fusilli ≡ 200g Pasta" — identity throughout.
        new(FusilliEdgeId, CookConfirmFixture.PastaId, 200m, CookConfirmFixture.GramUnitId,
            FusilliId, 100m, CookConfirmFixture.GramUnitId),
        // "100g Orzo ≡ 200g Pasta", declared in grams — Hop C must convert into Orzo's own tbsp stock unit.
        new(OrzoEdgeId, CookConfirmFixture.PastaId, 200m, CookConfirmFixture.GramUnitId,
            OrzoId, 100m, CookConfirmFixture.GramUnitId),
        // Target unit (EachUnitId) has no registered conversion from the line's GramUnitId — Hop A fails.
        new(BarleyEdgeId, CookConfirmFixture.PastaId, 1m, CookConfirmFixture.EachUnitId,
            BarleyId, 1m, CookConfirmFixture.GramUnitId),
        // Declared in grams (Hop A/B fine) but Quinoa's own stock unit (EachUnitId) has no registered
        // conversion from grams — Hop C fails.
        new(QuinoaEdgeId, CookConfirmFixture.PastaId, 200m, CookConfirmFixture.GramUnitId,
            QuinoaId, 50m, CookConfirmFixture.GramUnitId),
        // "100g GrainMedley ≡ 200g Pasta" — identity throughout; the substitute is a PARENT, so DM-19
        // rollup (factor 1.0) offers each live variant child individually.
        new(GrainMedleyEdgeId, CookConfirmFixture.PastaId, 200m, CookConfirmFixture.GramUnitId,
            GrainMedleyParentId, 100m, CookConfirmFixture.GramUnitId),
        // "1 ea Garlic Powder ≡ 1 ea Garlic" — targets a PARENT (Garlic) with ZERO live variant children
        // in this fixture (arbiter ruling 6).
        new(GarlicPowderEdgeId, CookConfirmFixture.GarlicParentId, 1m, CookConfirmFixture.EachUnitId,
            GarlicPowderId, 1m, CookConfirmFixture.EachUnitId),
    ];
}

/// <summary>
/// Unit converter for the substitution picker tests — identity for any same-unit pair, ONE genuine
/// non-identity conversion (Orzo: 10g per tbsp, so Hop C actually exercises real math), and failure for
/// every other pair — including Pasta gram→each (Barley's Hop A) and Quinoa gram→each (its own Hop C) —
/// so those two candidates are provably disqualified by a real conversion failure, not a test artifact.
/// </summary>
public sealed class FakeSubstitutionUnitConverter : IUnitConverter
{
    public Task<Result<decimal>> ConvertAsync(
        Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default)
    {
        if (fromUnitId == toUnitId)
            return Task.FromResult(Result<decimal>.Success(amount));

        if (productId == CookSubstitutionFixtureData.OrzoId &&
            fromUnitId == CookConfirmFixture.GramUnitId && toUnitId == CookConfirmFixture.TbspUnitId)
            return Task.FromResult(Result<decimal>.Success(amount / 10m)); // 10g per tbsp

        return Task.FromResult(Result<decimal>.Failure(
            Error.Custom("Test.NoPath", "No conversion path.")));
    }
}

/// <summary>
/// L4 WebApplicationFactory for the substitute-aware picker tests (plantry-aqpa.3). Mirrors
/// <see cref="CookConfirmFragmentFactory"/> / <c>CookPostFactory</c> exactly, except the catalog/stock
/// readers are seeded with the extra substitute products, the unit converter supports one real
/// non-identity conversion, and <see cref="ISubstitutionReader"/> carries several edges targeting Pasta.
/// </summary>
internal sealed class CookSubstitutionFactory : WebApplicationFactory<Program>
{
    public Recipe Recipe { get; } = CookConfirmFixture.Build();
    public Guid RecipeId => Recipe.Id.Value;
    public Guid PastaIngredientId => Recipe.Ingredients.Single(i => i.ProductId == CookConfirmFixture.PastaId).Id.Value;
    public Guid GarlicIngredientId => Recipe.Ingredients.Single(i => i.ProductId == CookConfirmFixture.GarlicParentId).Id.Value;

    public RecordingFakeCookInventoryConsumer Consumer { get; } = new();

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
                new FakeCookCatalogReader(CookSubstitutionFixtureData.Products(), CookConfirmFixture.UnitCodes()));

            services.RemoveAll<IInventoryStockReader>();
            services.AddSingleton<IInventoryStockReader>(
                new FakeCookStockReader(CookSubstitutionFixtureData.Stock()));

            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeSubstitutionUnitConverter());
            services.AddFakeQuantityFormatter();

            services.RemoveAll<ISubstitutionReader>();
            services.AddSingleton<ISubstitutionReader>(
                CookSubstitutionFixtureData.Edges().Aggregate(
                    new FakeCookSubstitutionReader(), (reader, edge) => reader.Add(edge)));

            services.RemoveAll<IInventoryConsumer>();
            services.AddSingleton<IInventoryConsumer>(Consumer);

            services.RemoveAll<ICookEventRepository>();
            services.AddSingleton<ICookEventRepository>(new FakeCookEventRepository());

            services.RemoveAll<ICatalogWriter>();
            services.AddSingleton<ICatalogWriter>(new FakeCatalogWriter());

            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(new FakeTagRepository(new Dictionary<TagId, string>()));

            services.RemoveAll<IPriceReader>();
            services.AddSingleton<IPriceReader>(new FakeDetailPriceReader(new Dictionary<Guid, PricePoint>()));
        });
    }
}

/// <summary>Tiny fluent helper so <see cref="HttpClient"/> setup reads as one expression above.</summary>
internal static class HttpClientExtensions
{
    public static HttpClient With(this HttpClient client, Action<HttpClient> configure)
    {
        configure(client);
        return client;
    }
}
