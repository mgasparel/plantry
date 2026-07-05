using System.Net;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// plantry-qno9 — POST binding tests for the four-field conversion equation. The in-sheet prompt lets
/// the author state a cross-measure fact against ANY unit pair (LEFT amount+unit on the product stock
/// dimension = RIGHT amount+unit on the recipe-line dimension). The page model is server-authoritative
/// for the factor: it computes <c>factor = rightAmount / leftAmount</c>, <c>from = left unit</c>,
/// <c>to = right unit</c>, and writes the conversion via <see cref="Plantry.Recipes.Application.ICatalogWriter"/>.
/// Amounts ≤ 0 (or missing) never write a ProductConversion.
/// </summary>
public sealed class RecipeEditorConversionEquationTests : IDisposable
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

    /// <summary>
    /// A tracked line measured in a cross-dimension unit (ea) against a product stocked in g, with the
    /// four equation fields posted as "100 g = 1 ea", saves the recipe and writes exactly
    /// (product, from = g, to = ea, factor = 1/100) — proving the server computes factor = right/left and
    /// stores the left→right unit pair verbatim, not a recipeUnit→productDefault assumption.
    /// </summary>
    [Fact]
    public async Task Four_field_equation_computes_factor_right_over_left_and_writes_left_to_right_pair()
    {
        var client = AuthenticatedClient();
        var token = await GetAntiforgeryTokenAsync(client);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.Name",            "Cashew Bowl"),
            new("Input.DefaultServings", "2"),
            new("Input.Lines[0].Ordinal",   "0"),
            new("Input.Lines[0].ProductId", RecipeEditorFixture.PastaId.ToString()),   // stocked in g (tracked)
            new("Input.Lines[0].Quantity",  "2"),
            new("Input.Lines[0].UnitId",    RecipeEditorFixture.EachUnitId.ToString()), // ea → cross-dimension gap
            // Four-field equation: LEFT "100 g" = RIGHT "1 ea".
            new("Input.Lines[0].ConversionLeftAmount",  "100"),
            new("Input.Lines[0].ConversionLeftUnitId",  RecipeEditorFixture.GramUnitId.ToString()),
            new("Input.Lines[0].ConversionRightAmount", "1"),
            new("Input.Lines[0].ConversionRightUnitId", RecipeEditorFixture.EachUnitId.ToString()),
        };

        var response = await client.PostAsync("/Recipes/New", new FormUrlEncodedContent(fields));

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after save, got {(int)response.StatusCode}.");

        var written = Assert.Single(_factory.CatalogWriter.ConversionsAdded);
        Assert.Equal(RecipeEditorFixture.PastaId, written.ProductId);
        Assert.Equal(RecipeEditorFixture.GramUnitId, written.FromUnitId);  // from = LEFT unit
        Assert.Equal(RecipeEditorFixture.EachUnitId, written.ToUnitId);    // to   = RIGHT unit
        Assert.Equal(1m / 100m, written.Factor);                           // factor = right / left
    }

    /// <summary>
    /// AC6: a non-positive left amount never writes a ProductConversion. The equation is ignored and no
    /// conversion is recorded (the always-succeeds test converter still lets the save complete here; the
    /// "re-surface NeedsConversion when the entry does not connect" guard is covered at the AuthorRecipe
    /// unit level).
    /// </summary>
    [Fact]
    public async Task Non_positive_amount_writes_no_conversion()
    {
        var client = AuthenticatedClient();
        var token = await GetAntiforgeryTokenAsync(client);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.Name",            "Zero Guard"),
            new("Input.DefaultServings", "2"),
            new("Input.Lines[0].Ordinal",   "0"),
            new("Input.Lines[0].ProductId", RecipeEditorFixture.PastaId.ToString()),
            new("Input.Lines[0].Quantity",  "2"),
            new("Input.Lines[0].UnitId",    RecipeEditorFixture.EachUnitId.ToString()),
            new("Input.Lines[0].ConversionLeftAmount",  "0"),    // ≤ 0 → never writes
            new("Input.Lines[0].ConversionLeftUnitId",  RecipeEditorFixture.GramUnitId.ToString()),
            new("Input.Lines[0].ConversionRightAmount", "1"),
            new("Input.Lines[0].ConversionRightUnitId", RecipeEditorFixture.EachUnitId.ToString()),
        };

        await client.PostAsync("/Recipes/New", new FormUrlEncodedContent(fields));

        Assert.Empty(_factory.CatalogWriter.ConversionsAdded);
    }
}
