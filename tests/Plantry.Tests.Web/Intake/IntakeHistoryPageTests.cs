using System.Net;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Intake;

/// <summary>
/// L4 fragment tests for <c>/Intake/History</c> (receipt-intake-history.md H5) — every status renders
/// (Committed, Ready, Failed, Discarded), a Committed row's store name links to its session detail, a
/// Ready row gets a Resume action, and household scoping excludes a foreign session.
/// </summary>
public sealed class IntakeHistoryPageTests : IClassFixture<IntakeHistorySessionFragmentFactory>
{
    private readonly IntakeHistorySessionFragmentFactory _factory;

    public IntakeHistoryPageTests(IntakeHistorySessionFragmentFactory factory) => _factory = factory;

    private HttpClient AuthClient() => _factory.CreateAuthClient(IntakeHistoryFixture.HouseholdAId);

    [Fact]
    public async Task Renders_every_status_with_the_right_badge()
    {
        var resp = await AuthClient().GetAsync("/Intake/History");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Contains("Costco Wholesale", html);
        Assert.Contains("badge--success", html); // Committed
        Assert.Contains("Being reviewed", html); // Ready
        Assert.Contains("badge--danger", html);  // Failed
        Assert.Contains("Discarded", html);
    }

    [Fact]
    public async Task Committed_store_name_links_to_the_session_detail()
    {
        var resp = await AuthClient().GetAsync("/Intake/History");
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Contains($"/Intake/Session/{_factory.Committed.Id.Value}", html);
    }

    [Fact]
    public async Task Ready_row_gets_a_resume_action()
    {
        var resp = await AuthClient().GetAsync("/Intake/History");
        var html = await resp.Content.ReadAsStringAsync();

        // Review's own route is "{id:guid}" (path-segment), so Url.Page produces a path, not "?id=".
        Assert.Contains($"/Intake/Review/{_factory.Ready.Id.Value}", html);
        Assert.Contains("Resume", html);
    }

    [Fact]
    public async Task Foreign_household_session_never_appears()
    {
        var resp = await AuthClient().GetAsync("/Intake/History");
        var html = await resp.Content.ReadAsStringAsync();

        Assert.DoesNotContain(_factory.ForeignCommitted.Id.Value.ToString(), html);
    }

    [Fact]
    public async Task Unauthenticated_request_is_challenged()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var resp = await client.GetAsync("/Intake/History");
        Assert.True(resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Found or HttpStatusCode.Redirect);
    }
}
