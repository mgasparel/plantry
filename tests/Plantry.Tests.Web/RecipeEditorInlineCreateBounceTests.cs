using System.Net;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

// ── plantry-5oek: a blank-quantity inline-create line must not ghost on the failed-save bounce ──
//
// Reported dogfood bug: creating a NEW tracked product inline for an ingredient and leaving the quantity
// blank passed the client and bounced server-side (R5, Recipes.TrackedRequiresQuantity). The re-rendered
// editor previously seeded the inline-create row with hard-coded null/false fields, so the row lost its
// typed name and rendered as a blank "ghost" entry. This L4 test drives the JS-independent POST backstop
// and asserts the bounced row round-trips its typed name + tracked flag into the Alpine rows[] state — so
// rowSummary shows the name and rowNeedsQtyUnit flags it, never a ghost.

/// <summary>
/// POST bounce path (plantry-5oek Defect 2): posting an inline tracked-create ingredient with a blank
/// quantity is rejected by R5, and the re-rendered editor must re-seed the row with its typed
/// <c>newStapleName</c> + <c>newIsTracked</c> intact (not the old hard-coded null/false ghost), while
/// minting no orphan catalog product (the R5 pre-check runs before any Catalog write).
/// </summary>
public sealed class RecipeEditorInlineCreateBouncePostTests : IDisposable
{
    private readonly RecipeEditorPostFactory _factory = new();
    private static readonly HtmlParser Parser = new();

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
    public async Task Blank_quantity_inline_create_bounce_reseeds_row_with_typed_name_not_ghost()
    {
        var client = AuthenticatedClient();
        var token = await GetAntiforgeryTokenAsync(client);

        const string typedName = "Cardamom Pods";

        // Inline tracked create (no ProductId) with a supplied default unit but a BLANK quantity → R5
        // rejects with Recipes.TrackedRequiresQuantity before any Catalog write.
        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.Name",            "Ghost Row Repro"),
            new("Input.DefaultServings", "2"),
            new("Input.Lines[0].Ordinal",                "0"),
            new("Input.Lines[0].ProductId",              ""),        // inline create — no chosen product
            new("Input.Lines[0].NewStapleName",          typedName),
            new("Input.Lines[0].NewIsTracked",           "true"),
            new("Input.Lines[0].NewStapleDefaultUnitId", RecipeEditorFixture.GramUnitId.ToString()),
            new("Input.Lines[0].UnitId",                 RecipeEditorFixture.GramUnitId.ToString()),
            new("Input.Lines[0].Quantity",               ""),        // blank → R5 rejects
        };

        var rejected = await client.PostAsync("/Recipes/New", new FormUrlEncodedContent(fields));

        // Invalid → the page re-renders (200) rather than redirecting; nothing persisted, no orphan product.
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Null(_factory.RecipeRepo.LastAdded);

        var body = await rejected.Content.ReadAsStringAsync();
        var doc = Parser.ParseDocument(body);
        var editor = doc.QuerySelector("#recipe-editor")
            ?? throw new InvalidOperationException("#recipe-editor not found.");

        // AngleSharp returns the HTML-decoded x-data attribute — the JS object literal that carries the
        // JsonSerializer-emitted rows array (compact, camelCase keys).
        var xData = editor.GetAttribute("x-data")
            ?? throw new InvalidOperationException("x-data attribute not found.");

        // Defect 2: the bounced inline-create row re-seeds with its typed name + tracked flag — the exact
        // Alpine rows[] entry that rowSummary renders and rowNeedsQtyUnit flags, never a blank ghost.
        Assert.Contains($"\"newStapleName\":\"{typedName}\"", xData);
        Assert.Contains("\"newIsTracked\":true", xData);
        // The row carries no productId (it is an inline create) — the fix must not fabricate one.
        Assert.Contains("\"productId\":\"\"", xData);
        // The row-scoped R5 SaveError banner names the offending product + its 1-based line (server intact).
        Assert.Contains(typedName, body);
        Assert.Contains("line 1", body);
    }
}
