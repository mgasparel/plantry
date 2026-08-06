using System.Net;
using System.Text.RegularExpressions;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Catalog;

/// <summary>
/// L4 Web integration tests for the "Homemade (not a restock suggestion)" toggle on the Product
/// Detail (edit) page (plantry-sn6v step 5). The page seeds <c>Input.IsProduced</c> from the
/// aggregate in <c>PopulateInputFromEntity</c> and OnPost passes it into
/// <c>UpdateProductCommand</c>, which applies it unconditionally — so a broken seed would let any
/// unrelated edit to a yield product silently clear the flag and reintroduce the exact
/// "buy your own leftovers" bug this ticket fixes. Two facts pin the GET→POST round trip:
/// (a) an edit changing only the name preserves a produced product's flag, and (b) an explicit
/// flip in each direction persists.
///
/// Reuses <see cref="ProductDetailTrackStockFactory"/> (same directory) — in-memory fakes, no
/// database.
/// </summary>
public sealed class ProductDetailIsProducedTests : IDisposable
{
    private static readonly Guid HouseholdId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private readonly ProductDetailTrackStockFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient AuthClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdId.ToString());
        return client;
    }

    private async Task<string> GetAntiforgeryTokenAsync(HttpClient client, Guid productId)
    {
        var html = await (await client.GetAsync($"/Catalog/Products/{productId}")).Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the Detail page.");
        return match.Groups[1].Value;
    }

    /// <summary>The full edit form as a browser would resubmit it after a GET — every rendered
    /// field carried back, with the caller choosing the name and the IsProduced checkbox state.
    /// <paramref name="isProduced"/> null means the field is omitted from the post entirely
    /// (the unchecked-checkbox-without-hidden-companion shape).</summary>
    private static FormUrlEncodedContent EditForm(
        string token, Plantry.Pantry.Domain.Product product, string name, bool? isProduced) =>
        new(
        [
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            new KeyValuePair<string, string>("Input.Name", name),
            new KeyValuePair<string, string>("Input.DefaultUnitId", product.DefaultUnitId.Value.ToString()),
            new KeyValuePair<string, string>("Input.TrackStock", product.TrackStock ? "true" : "false"),
            .. isProduced is { } p
                ? new[] { new KeyValuePair<string, string>("Input.IsProduced", p ? "true" : "false") }
                : [],
        ]);

    [Fact(DisplayName = "Edit — a produced product renders a checked Homemade checkbox, and a name-only edit preserves the flag")]
    public async Task Edit_ProducedProduct_RoundTrip_PreservesIsProduced()
    {
        var client = AuthClient();
        var productId = _factory.TrackedStandaloneId;
        var product = _factory.ProductRepo.Items.Single(p => p.Id.Value == productId);
        product.SetProduced(true, ProductDetailTrackStockFactory.Clock);

        // GET: the checkbox is seeded from the aggregate (PopulateInputFromEntity) and renders checked.
        var html = await (await client.GetAsync($"/Catalog/Products/{productId}")).Content.ReadAsStringAsync();
        var checkbox = Regex.Match(html, "<input(?=[^>]*type=\"checkbox\")[^>]*name=\"Input\\.IsProduced\"[^>]*>");
        Assert.True(checkbox.Success, "The Homemade (IsProduced) checkbox was not rendered on the Detail page.");
        Assert.Contains("checked", checkbox.Value);

        // POST the form back changing only the name — the flag the form carried must survive.
        var token = await GetAntiforgeryTokenAsync(client, productId);
        var response = await client.PostAsync(
            $"/Catalog/Products/{productId}",
            EditForm(token, product, name: "Whole Milk (renamed)", isProduced: true));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("Whole Milk (renamed)", product.Name);
        Assert.True(product.IsProduced);
    }

    [Fact(DisplayName = "Edit — unchecking Homemade clears the flag; checking it on an ordinary product sets it")]
    public async Task Edit_IsProduced_FlipsInBothDirections()
    {
        var client = AuthClient();

        // Direction 1: a produced product posted without the checkbox value becomes not-produced
        // (the unchecked checkbox never reaches the wire; the bound default is false).
        var producedId = _factory.TrackedStandaloneId;
        var produced = _factory.ProductRepo.Items.Single(p => p.Id.Value == producedId);
        produced.SetProduced(true, ProductDetailTrackStockFactory.Clock);
        var token = await GetAntiforgeryTokenAsync(client, producedId);

        var clearResponse = await client.PostAsync(
            $"/Catalog/Products/{producedId}",
            EditForm(token, produced, name: produced.Name, isProduced: null));

        Assert.Equal(HttpStatusCode.Redirect, clearResponse.StatusCode);
        Assert.False(produced.IsProduced);

        // Direction 2: an ordinary purchased product posted with the checkbox checked becomes produced.
        var ordinaryId = _factory.UntrackedStandaloneId;
        var ordinary = _factory.ProductRepo.Items.Single(p => p.Id.Value == ordinaryId);
        Assert.False(ordinary.IsProduced);
        token = await GetAntiforgeryTokenAsync(client, ordinaryId);

        var setResponse = await client.PostAsync(
            $"/Catalog/Products/{ordinaryId}",
            EditForm(token, ordinary, name: ordinary.Name, isProduced: true));

        Assert.Equal(HttpStatusCode.Redirect, setResponse.StatusCode);
        Assert.True(ordinary.IsProduced);
    }
}
