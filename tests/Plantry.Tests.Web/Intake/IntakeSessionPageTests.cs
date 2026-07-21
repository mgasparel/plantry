using System.Net;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Intake;

/// <summary>
/// L4 fragment tests for <c>/Intake/Session/{id}</c> (receipt-intake-history.md H7/H8/H9/H10): the
/// Committed-only state guard (Ready → Review, other statuses → History, foreign/unknown → 404), receipt-
/// order line rendering, the dismissed-line struck-through treatment, the deep-link anchor id, and the
/// resolved/new-product line badges.
/// </summary>
public sealed class IntakeSessionPageTests : IClassFixture<IntakeHistorySessionFragmentFactory>
{
    private readonly IntakeHistorySessionFragmentFactory _factory;

    public IntakeSessionPageTests(IntakeHistorySessionFragmentFactory factory) => _factory = factory;

    private HttpClient AuthClient() => _factory.CreateAuthClient(IntakeHistoryFixture.HouseholdAId);

    [Fact]
    public async Task Renders_the_committed_session_with_store_and_stats()
    {
        var resp = await AuthClient().GetAsync($"/Intake/Session/{_factory.Committed.Id.Value}");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Contains("Costco Wholesale", html);
        Assert.Contains("KS ORG SPINACH 454G", html);
        Assert.Contains("4023-1188-7734", html); // receipt number
    }

    [Fact]
    public async Task Resolved_line_links_to_the_pantry_product_detail()
    {
        var resp = await AuthClient().GetAsync($"/Intake/Session/{_factory.Committed.Id.Value}");
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Contains($"/Pantry/Products/Detail/{_factory.ExistingProduct.Id.Value}", html);
        Assert.Contains("Baby spinach", html);
    }

    [Fact]
    public async Task New_product_line_carries_the_new_product_badge()
    {
        var resp = await AuthClient().GetAsync($"/Intake/Session/{_factory.Committed.Id.Value}");
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Contains("New product", html);
    }

    [Fact]
    public async Task Dismissed_line_stays_visible_struck_through()
    {
        var resp = await AuthClient().GetAsync($"/Intake/Session/{_factory.Committed.Id.Value}");
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Contains("MEMBERSHIP RENEWAL", html);
        Assert.Contains("Dismissed during review", html);
        Assert.Contains("line--dismissed", html);
    }

    [Fact]
    public async Task Each_line_row_carries_its_deep_link_anchor_id()
    {
        var resp = await AuthClient().GetAsync($"/Intake/Session/{_factory.Committed.Id.Value}");
        var html = await resp.Content.ReadAsStringAsync();

        var firstLine = _factory.Committed.Lines.OrderBy(l => l.LineNo).First();
        Assert.Contains($"id=\"line-{firstLine.Id.Value}\"", html);
    }

    // ── State guard (H7) ─────────────────────────────────────────────────────

    [Fact]
    public async Task Ready_session_redirects_to_review()
    {
        var resp = await NoRedirectClient().GetAsync($"/Intake/Session/{_factory.Ready.Id.Value}");

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/Intake/Review", resp.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Failed_session_redirects_to_history()
    {
        var resp = await NoRedirectClient().GetAsync($"/Intake/Session/{_factory.Failed.Id.Value}");

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/Intake/History", resp.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Discarded_session_redirects_to_history()
    {
        var resp = await NoRedirectClient().GetAsync($"/Intake/Session/{_factory.Discarded.Id.Value}");

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/Intake/History", resp.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Unknown_session_id_is_not_found()
    {
        var resp = await AuthClient().GetAsync($"/Intake/Session/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Foreign_household_session_is_not_found()
    {
        var resp = await AuthClient().GetAsync($"/Intake/Session/{_factory.ForeignCommitted.Id.Value}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private HttpClient NoRedirectClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, IntakeHistoryFixture.HouseholdAId.ToString());
        return client;
    }
}
