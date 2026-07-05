using System.Net;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

// ── plantry-429l: surface/prefill a tracked line with null qty/unit ──────────────
//
// A recipe line authored while its product was untracked (null qty/unit legal) whose product is later
// flipped tracked is retroactively condemned by R5 on the next save. These L4 tests assert the editor
// surfaces the offending row (flagged + unit prefilled) on load, and that a save without a quantity
// fails with a row-scoped message that names the specific line — instead of an opaque global dead-end.

/// <summary>
/// GET load path: a stored recipe whose sole ingredient is a now-tracked product with null qty/unit
/// must hydrate the row as tracked (<c>isUntracked:false</c>) with its unit prefilled to the product
/// default and quantity left blank — the exact state the reactive <c>rowNeedsQtyUnit()</c> warning keys on.
/// </summary>
public sealed class RecipeEditorTrackedFlipLoadTests(RecipeEditorFragmentFactory factory)
    : IClassFixture<RecipeEditorFragmentFactory>
{
    private static readonly HtmlParser Parser = new();

    private async Task<string> GetEditPageAsync(Guid recipeId)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeEditorFixture.HouseholdAId.ToString());
        var response = await client.GetAsync($"/Recipes/{recipeId}/Edit");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Flagged_row_hydrates_as_tracked_with_unit_prefilled_and_qty_blank()
    {
        var html = await GetEditPageAsync(RecipeEditorFixture.FlipToTrackedRecipeId.Value);
        var doc = Parser.ParseDocument(html);
        var editor = doc.QuerySelector("#recipe-editor")
            ?? throw new InvalidOperationException("#recipe-editor not found.");

        // AngleSharp returns the HTML-decoded attribute value — the x-data JS object literal, which
        // includes the JsonSerializer-emitted rows array (compact, camelCase keys).
        var xData = editor.GetAttribute("x-data")
            ?? throw new InvalidOperationException("x-data attribute not found.");

        // The single ingredient row references the now-tracked Tomato product.
        Assert.Contains($"\"productId\":\"{RecipeEditorFixture.TomatoId}\"", xData);
        // Real tracked state hydrated (previously hard-coded false regardless of the product).
        Assert.Contains("\"isUntracked\":false", xData);
        // Quantity is never prefilled — the blank qty keeps the row flagged until the author acts.
        Assert.Contains("\"qty\":\"\"", xData);
        // Part A: the unit is prefilled to the product's default unit (Gram) so the author only supplies qty.
        Assert.Contains($"\"unitId\":\"{RecipeEditorFixture.GramUnitId}\"", xData);

        // The row-scoped warning + reactive predicate are present in the editor markup.
        Assert.Contains("rowNeedsQtyUnit(row)", html);
    }
}

/// <summary>
/// POST save path: submitting a tracked ingredient with no quantity fails with the row-scoped
/// <c>Recipes.TrackedRequiresQuantity</c> message that names the offending product + 1-based line;
/// supplying the quantity then saves. Uses <see cref="RecipeEditorPostFactory"/> whose product reader
/// reports Tomato as tracked and maps its default unit, so the R5 rule fires deterministically.
/// </summary>
public sealed class RecipeEditorTrackedFlipPostTests : IDisposable
{
    private readonly RecipeEditorPostFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient AuthenticatedClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeEditorFixture.HouseholdAId.ToString());
        return client;
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var html = await (await client.GetAsync("/Recipes/New")).Content.ReadAsStringAsync();
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the create page.");
        return match.Groups[1].Value;
    }

    [Fact]
    public async Task Save_without_quantity_surfaces_row_scoped_message_then_succeeds_with_quantity()
    {
        var client = AuthenticatedClient();
        var token  = await GetAntiforgeryTokenAsync(client);

        // First save: tracked Tomato line with the unit prefilled but NO quantity → R5 rejects.
        var noQtyFields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.Name",            "Flip Save Test"),
            new("Input.DefaultServings", "2"),
            new("Input.Lines[0].Ordinal",   "0"),
            new("Input.Lines[0].ProductId", RecipeEditorFixture.TomatoId.ToString()),
            new("Input.Lines[0].Quantity",  ""),                                       // missing
            new("Input.Lines[0].UnitId",    RecipeEditorFixture.GramUnitId.ToString()), // prefilled
        };

        var rejected = await client.PostAsync("/Recipes/New", new FormUrlEncodedContent(noQtyFields));

        // Invalid → the page re-renders (200) rather than redirecting.
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        var rejectedBody = await rejected.Content.ReadAsStringAsync();
        // Row-scoped: names the specific product and its 1-based line number, not just a generic banner.
        Assert.Contains("Canned Tomatoes", rejectedBody);
        Assert.Contains("line 1", rejectedBody);
        Assert.Null(_factory.RecipeRepo.LastAdded); // nothing persisted

        // Second save: same line, now WITH a quantity → saves and redirects.
        var token2 = await GetAntiforgeryTokenAsync(client);
        var okFields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token2),
            new("Input.Name",            "Flip Save Test"),
            new("Input.DefaultServings", "2"),
            new("Input.Lines[0].Ordinal",   "0"),
            new("Input.Lines[0].ProductId", RecipeEditorFixture.TomatoId.ToString()),
            new("Input.Lines[0].Quantity",  "300"),
            new("Input.Lines[0].UnitId",    RecipeEditorFixture.GramUnitId.ToString()),
        };

        var saved = await client.PostAsync("/Recipes/New", new FormUrlEncodedContent(okFields));

        Assert.True(
            saved.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after supplying the quantity, got {(int)saved.StatusCode}.");
        Assert.NotNull(_factory.RecipeRepo.LastAdded);
        var ingredient = Assert.Single(_factory.RecipeRepo.LastAdded!.Ingredients);
        Assert.Equal(300m, ingredient.Quantity);
        Assert.Equal(RecipeEditorFixture.GramUnitId, ingredient.UnitId);
    }
}
