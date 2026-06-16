using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 tests for the Shopping page's <c>OnPostAddItemAsync</c> handler (plantry-3dh.1, items A/B/C).
///
/// The add form posts back the list fragment as the main swap PLUS the add form as an out-of-band
/// swap (<c>_AddPostResult</c>), so these tests assert on both fragments:
/// <list type="bullet">
///   <item><b>A</b> — on a successful add the OOB form re-renders with an empty Custom Item input.</item>
///   <item><b>B</b> — posting both a product and stale free-text succeeds (product wins), no error.</item>
///   <item><b>C</b> — posting neither renders the validation alert in the form.</item>
/// </list>
/// A fresh factory is created per test so each starts from a clean fixture list (the fake repository
/// mutates the shared list instance in place).
/// </summary>
public sealed class ShoppingAddItemTests : IDisposable
{
    private static readonly HtmlParser Parser = new();
    private readonly ShoppingListFragmentFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient AuthenticatedClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            ShoppingListFixture.HouseholdAId.ToString());
        return client;
    }

    /// <summary>GET the Shopping page to harvest a paired antiforgery token + cookie before POSTing.</summary>
    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var html = await (await client.GetAsync("/Shopping")).Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the Shopping page.");
        return match.Groups[1].Value;
    }

    private async Task<HttpResponseMessage> PostAddItemAsync(
        HttpClient client, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var token = await GetAntiforgeryTokenAsync(client);
        var allFields = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        allFields.AddRange(fields);
        return await client.PostAsync("/Shopping?handler=AddItem", new FormUrlEncodedContent(allFields));
    }

    // ── C: validation feedback ────────────────────────────────────────────────

    [Fact(DisplayName = "AddItem — neither product nor free-text renders the validation alert")]
    public async Task AddItem_NeitherProvided_RendersValidationError()
    {
        var client = AuthenticatedClient();

        var response = await PostAddItemAsync(client, []);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var doc = Parser.ParseDocument(html);

        var alert = doc.QuerySelector("#add-item-form .alert--danger");
        Assert.NotNull(alert);
        Assert.Contains("Choose a product or enter an item name.", alert!.TextContent);
    }

    // ── B: product wins over stale free-text ──────────────────────────────────

    [Fact(DisplayName = "AddItem — product plus stale custom text succeeds (product wins), no error")]
    public async Task AddItem_BothProvided_ProductWins_NoError()
    {
        var client = AuthenticatedClient();

        // Milk is already unchecked on the fixture list, so the product path merges and succeeds.
        var response = await PostAddItemAsync(client,
        [
            new("Input.ProductId", ShoppingListFixture.MilkProductId.ToString()),
            new("Input.FreeText", "stale leftover text"),
            new("Input.Quantity", "1"),
        ]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        // No validation error of either kind: the old "both supplied" guard is gone (product wins),
        // and the "neither" guard must not trip when a product is present.
        Assert.DoesNotContain("Choose a product or enter an item name.", html);
        Assert.DoesNotContain("not both or neither", html);

        var doc = Parser.ParseDocument(html);
        Assert.Null(doc.QuerySelector("#add-item-form .alert--danger"));
    }

    // ── A: form resets on success ─────────────────────────────────────────────

    [Fact(DisplayName = "AddItem — successful add returns OOB form with cleared Custom Item input")]
    public async Task AddItem_Success_ClearsFreeTextInput()
    {
        var client = AuthenticatedClient();

        var response = await PostAddItemAsync(client,
        [
            new("Input.FreeText", "Paper towels"),
        ]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var doc = Parser.ParseDocument(html);

        // The add form comes back as an out-of-band swap so htmx replaces it in place.
        var form = doc.QuerySelector("#add-item-form");
        Assert.NotNull(form);
        Assert.Equal("true", form!.GetAttribute("hx-swap-oob"));

        // The Custom Item input must be cleared (no residual "Paper towels" value) — item A.
        var freeText = doc.QuerySelector("#add-item-form input[name='Input.FreeText']");
        Assert.NotNull(freeText);
        Assert.True(
            string.IsNullOrEmpty(freeText!.GetAttribute("value")),
            $"Expected cleared Custom Item input, got value='{freeText.GetAttribute("value")}'.");
    }
}
