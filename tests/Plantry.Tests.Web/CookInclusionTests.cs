using System.Net;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// Handler-level tests for the Cook page's recipe-composition rendering + resolution mapping
/// (plantry-fqb0.9, recipe-composition.md §6, D6/D7).
///
/// Fixture: parent "Nachos" (4 servings) = one direct leaf (Chips, 200g) + an inclusion of
/// "Nacho Cheese" (2 servings). The sub (4 servings) has Cashews (100g) and Nutritional Yeast (20g),
/// so at the default 4 servings the inclusion factor is 0.5 → Cashews expands to 50g, Yeast to 10g,
/// and the group header reads "Nacho Cheese — 2 servings".
///
/// GET assertions verify the grouped rendering (sub header, effective servings, path-qualified line
/// keys, whole-inclusion skip control). POST assertions verify the resolutions posted from those
/// path-qualified fields map to the right IInventoryConsumer.ConsumeAsync calls over a real CookRecipe.
/// </summary>
public sealed class CookInclusionTests : IDisposable
{
    private readonly CookInclusionFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private const int DefaultServings = 4;

    private HttpClient AuthenticatedClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, CookInclusionFixture.HouseholdAId.ToString());
        return client;
    }

    private string CookUrl => $"/Recipes/{_factory.RecipeId}/Cook";

    private async Task<string> GetPageAsync(int servings)
    {
        var client = AuthenticatedClient();
        var response = await client.GetAsync($"{CookUrl}?Servings={servings}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<string> GetAntiforgeryTokenAsync(HttpClient client, int servings)
    {
        var html = await (await client.GetAsync($"{CookUrl}?Servings={servings}"))
            .Content.ReadAsStringAsync();
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the Cook page.");
        return match.Groups[1].Value;
    }

    private async Task<HttpResponseMessage> PostCookAsync(
        HttpClient client, IEnumerable<KeyValuePair<string, string>> fields, int servings = DefaultServings)
    {
        var token = await GetAntiforgeryTokenAsync(client, servings);
        var allFields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Id", _factory.RecipeId.ToString()),
            new("Servings", servings.ToString()),
        };
        allFields.AddRange(fields);
        return await client.PostAsync(CookUrl, new FormUrlEncodedContent(allFields));
    }

    // ── GET: grouped rendering ──────────────────────────────────────────────────────────────────

    /// <summary>The inclusion renders as its own group: sub name header + effective servings + path key.</summary>
    [Fact]
    public async Task Inclusion_renders_as_group_with_header_and_path_keys()
    {
        var html = await GetPageAsync(DefaultServings);

        // Group header carries the sub-recipe name and its effective servings (2 = Inclusion.Servings × 1).
        Assert.Contains("Nacho Cheese", html, StringComparison.Ordinal);
        Assert.Contains("2 servings", html, StringComparison.Ordinal);

        // The whole-inclusion skip control is wired to the inclusion path (D7).
        Assert.Contains($"toggleInclusion('{_factory.InclusionPathKey}')", html, StringComparison.Ordinal);

        // Expanded sub lines carry path-qualified line keys (D6) and render with the per-line toolkit.
        Assert.Contains("Cashews", html, StringComparison.Ordinal);
        Assert.Contains("Nutritional Yeast", html, StringComparison.Ordinal);
        Assert.Contains($"toggleSkip('{_factory.LineKeyFor(CookInclusionFixture.CashewsId)}')", html,
            StringComparison.Ordinal);

        // A parent-product sub line renders the Variant Disambiguation Picker with the path-qualified
        // InclusionPickerSelections hidden input (C7/C11 on an expanded line).
        Assert.Contains("Choose variant", html, StringComparison.Ordinal);
        Assert.Contains("Oat Milk", html, StringComparison.Ordinal);
        Assert.Contains("Soy Milk", html, StringComparison.Ordinal);
        Assert.Contains($"name=\"InclusionPickerSelections[{_factory.LineKeyFor(CookInclusionFixture.MilkParentId)}]\"",
            html, StringComparison.Ordinal);
    }

    /// <summary>The group's effective servings rescale with the desired servings (D2).</summary>
    [Fact]
    public async Task Inclusion_effective_servings_rescale_with_desired_servings()
    {
        // At 8 servings (scale ×2), the inclusion's 2 servings scale to 4.
        var html = await GetPageAsync(servings: 8);
        Assert.Contains("×2", html, StringComparison.Ordinal);       // parent scale badge
        Assert.Contains("4 servings", html, StringComparison.Ordinal); // scaled group header
    }

    // ── POST: resolution mapping ────────────────────────────────────────────────────────────────

    /// <summary>A plain cook consumes the direct line and the scaled expanded sub lines (provenance path).</summary>
    [Fact]
    public async Task Plain_cook_consumes_direct_and_expanded_sub_lines()
    {
        var client = AuthenticatedClient();
        var response = await PostCookAsync(client, []);

        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found);

        var calls = _factory.Consumer.Calls;
        Assert.Equal(200m, calls.Single(c => c.ProductId == CookInclusionFixture.ChipsId).Quantity);
        Assert.Equal(50m, calls.Single(c => c.ProductId == CookInclusionFixture.CashewsId).Quantity);
        Assert.Equal(10m, calls.Single(c => c.ProductId == CookInclusionFixture.YeastId).Quantity);
    }

    /// <summary>Whole-inclusion skip (D7) drops every line under the inclusion in one action.</summary>
    [Fact]
    public async Task Whole_inclusion_skip_drops_all_sub_lines()
    {
        var client = AuthenticatedClient();
        var response = await PostCookAsync(client,
        [
            new("SkippedInclusions", _factory.InclusionPathKey),
        ]);

        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found);

        var calls = _factory.Consumer.Calls;
        // The direct line still cooks; both sub lines are dropped.
        Assert.Contains(calls, c => c.ProductId == CookInclusionFixture.ChipsId);
        Assert.DoesNotContain(calls, c => c.ProductId == CookInclusionFixture.CashewsId);
        Assert.DoesNotContain(calls, c => c.ProductId == CookInclusionFixture.YeastId);
    }

    /// <summary>A per-line skip inside the inclusion drops only that expanded line (path-qualified key).</summary>
    [Fact]
    public async Task Per_line_inclusion_skip_drops_only_that_line()
    {
        var client = AuthenticatedClient();
        var response = await PostCookAsync(client,
        [
            new("SkippedInclusionLineKeys", _factory.LineKeyFor(CookInclusionFixture.CashewsId)),
        ]);

        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found);

        var calls = _factory.Consumer.Calls;
        Assert.DoesNotContain(calls, c => c.ProductId == CookInclusionFixture.CashewsId);
        // The sibling sub line and the direct line still cook.
        Assert.Contains(calls, c => c.ProductId == CookInclusionFixture.YeastId);
        Assert.Contains(calls, c => c.ProductId == CookInclusionFixture.ChipsId);
    }

    /// <summary>
    /// A variant swap on an expanded inclusion line (C7/C11) consumes the CHOSEN variant, not the
    /// auto-selected default — proving the path-qualified InclusionPickerSelections → VariantAllocation
    /// mapping and the parent-line picker render branch (criterion 2 "swap ... on expanded lines").
    /// </summary>
    [Fact]
    public async Task Per_line_inclusion_variant_swap_consumes_chosen_variant()
    {
        var client = AuthenticatedClient();
        var lineKey = _factory.LineKeyFor(CookInclusionFixture.MilkParentId);
        var response = await PostCookAsync(client,
        [
            // Oat Milk is the auto-selected best (more stock); explicitly pick Soy Milk instead.
            new($"InclusionPickerSelections[{lineKey}]", CookInclusionFixture.SoyMilkId.ToString()),
        ]);

        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found);

        var calls = _factory.Consumer.Calls;
        // The chosen variant (Soy Milk) is consumed at the scaled quantity (60g × 0.5 factor = 30g);
        // neither the auto-selected Oat Milk nor the parent product is consumed.
        var soyCall = calls.Single(c => c.ProductId == CookInclusionFixture.SoyMilkId);
        Assert.Equal(30m, soyCall.Quantity);
        Assert.DoesNotContain(calls, c => c.ProductId == CookInclusionFixture.OatMilkId);
        Assert.DoesNotContain(calls, c => c.ProductId == CookInclusionFixture.MilkParentId);
    }

    /// <summary>A per-line quantity override on an expanded inclusion line changes its consume quantity.</summary>
    [Fact]
    public async Task Per_line_inclusion_quantity_override_changes_consume_quantity()
    {
        var client = AuthenticatedClient();
        var lineKey = _factory.LineKeyFor(CookInclusionFixture.CashewsId);
        var response = await PostCookAsync(client,
        [
            new($"InclusionQuantityOverrides[{lineKey}]", "25"),
        ]);

        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found);

        var cashewsCall = _factory.Consumer.Calls.Single(c => c.ProductId == CookInclusionFixture.CashewsId);
        Assert.Equal(25m, cashewsCall.Quantity);
    }
}
