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
    public async Task Renders_a_source_badge_for_both_receipt_and_manual_rows()
    {
        var resp = await AuthClient().GetAsync("/Intake/History");
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Contains("Costco Wholesale", html); // receipt session
        Assert.Contains("Corner Store", html);      // manual session
        Assert.Contains(">Receipt<", html);
        Assert.Contains(">Manual<", html);
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
    public async Task Quick_add_sheet_offers_the_three_way_model_distinctly()
    {
        // plantry-45ba.4: the mobile quick-add sheet (shared _Layout, rendered on every authenticated
        // page) must offer scan/enter-a-purchase/count-stock as three distinct options rather than
        // conflating manual intake with inventory counting.
        var resp = await AuthClient().GetAsync("/Intake/History");
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Contains("Scan receipt", html);
        Assert.Contains("Enter a purchase", html);
        Assert.Contains("Count stock", html);
        Assert.Contains("href=\"/Intake/Manual\"", html);
        Assert.Contains("href=\"/Intake/Upload\"", html);
        Assert.Contains("href=\"/Pantry\"", html);
        Assert.DoesNotContain("Add manually", html); // old two-way copy that conflated counting with intake
    }

    [Fact]
    public async Task Unauthenticated_request_is_challenged()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var resp = await client.GetAsync("/Intake/History");
        Assert.True(resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Found or HttpStatusCode.Redirect);
    }
}
