using System.Net;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

// ── plantry-bke5: untracked ingredients can carry a quantity in the recipe editor ────────────────
//
// The domain layer has always allowed an untracked recipe line to carry a quantity/unit (R5 in
// AuthorRecipe.cs only *requires* qty/unit when TrackStock is true — it never forbids one otherwise).
// The editor UI, however, hid the Quantity/Unit fields and forcibly cleared any typed value the moment
// an untracked product was picked, making it impossible to enter (or see) a quantity for such a line.
// These L4 tests pin the round trip: a quantity survives product selection, saves, and reloads; and an
// untracked line with NO quantity still saves (guarding against accidentally making it required).

/// <summary>
/// GET load path: a stored recipe whose sole ingredient is the untracked Salt staple with a real
/// quantity + unit must hydrate the row with that quantity/unit populated — not blank — so the editor
/// round-trips a value the domain has always allowed.
/// </summary>
public sealed class RecipeEditorUntrackedQuantityLoadTests(RecipeEditorFragmentFactory factory)
    : IClassFixture<RecipeEditorFragmentFactory>
{
    private static readonly HtmlParser Parser = new();

    [Fact]
    public async Task Untracked_row_with_quantity_hydrates_qty_and_unit_populated()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeEditorFixture.HouseholdAId.ToString());

        var response = await client.GetAsync(
            $"/Recipes/{RecipeEditorFixture.UntrackedWithQuantityRecipeId.Value}/Edit");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        var doc = Parser.ParseDocument(html);
        var editor = doc.QuerySelector("#recipe-editor")
            ?? throw new InvalidOperationException("#recipe-editor not found.");
        var xData = editor.GetAttribute("x-data")
            ?? throw new InvalidOperationException("x-data attribute not found.");

        Assert.Contains($"\"productId\":\"{RecipeEditorFixture.SaltId}\"", xData);
        Assert.Contains("\"isUntracked\":true", xData);
        // The quantity/unit are NOT wiped for an untracked row — this is the round-trip this bug broke.
        Assert.Contains("\"qty\":\"5\"", xData);
        Assert.Contains($"\"unitId\":\"{RecipeEditorFixture.GramUnitId}\"", xData);

        // The Quantity/Unit x-show gates no longer exclude untracked rows (plantry-bke5).
        Assert.Contains("x-show=\"draft.isUntracked || draft.productId || draft.newIsTracked\"", html);
    }
}

/// <summary>
/// POST save path: an untracked ingredient carrying a quantity persists it, and an untracked ingredient
/// with no quantity still saves successfully (qty/unit remain optional, never required, for untracked lines).
/// </summary>
public sealed class RecipeEditorUntrackedQuantityPostTests : IDisposable
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
    public async Task Untracked_ingredient_with_quantity_persists_it()
    {
        var client = AuthenticatedClient();
        var token = await GetAntiforgeryTokenAsync(client);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.Name",            "Salted Test"),
            new("Input.DefaultServings", "2"),
            new("Input.Lines[0].Ordinal",   "0"),
            new("Input.Lines[0].ProductId", RecipeEditorFixture.SaltId.ToString()),
            new("Input.Lines[0].Quantity",  "5"),
            new("Input.Lines[0].UnitId",    RecipeEditorFixture.GramUnitId.ToString()),
        };

        var saved = await client.PostAsync("/Recipes/New", new FormUrlEncodedContent(fields));

        Assert.True(
            saved.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after saving an untracked line with a quantity, got {(int)saved.StatusCode}.");
        Assert.NotNull(_factory.RecipeRepo.LastAdded);
        var ingredient = Assert.Single(_factory.RecipeRepo.LastAdded!.Ingredients);
        Assert.Equal(5m, ingredient.Quantity);
        Assert.Equal(RecipeEditorFixture.GramUnitId, ingredient.UnitId);
    }

    [Fact]
    public async Task Untracked_ingredient_with_no_quantity_still_saves()
    {
        var client = AuthenticatedClient();
        var token = await GetAntiforgeryTokenAsync(client);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.Name",            "Unquantified Salt Test"),
            new("Input.DefaultServings", "2"),
            new("Input.Lines[0].Ordinal",   "0"),
            new("Input.Lines[0].ProductId", RecipeEditorFixture.SaltId.ToString()),
            new("Input.Lines[0].Quantity",  ""),
            new("Input.Lines[0].UnitId",    ""),
        };

        var saved = await client.PostAsync("/Recipes/New", new FormUrlEncodedContent(fields));

        Assert.True(
            saved.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after saving an untracked line with no quantity, got {(int)saved.StatusCode}.");
        Assert.NotNull(_factory.RecipeRepo.LastAdded);
        var ingredient = Assert.Single(_factory.RecipeRepo.LastAdded!.Ingredients);
        Assert.Null(ingredient.Quantity);
        Assert.Null(ingredient.UnitId);
    }
}
