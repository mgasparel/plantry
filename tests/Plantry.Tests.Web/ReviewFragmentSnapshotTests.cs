using System.Net;
using Plantry.Intake.Domain;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// Approval/snapshot tests of the intake review-form htmx fragments. Each test fetches the real review page as
/// household A, extracts one fragment (a <c>_ReviewRow</c> in a specific state, or the commit bar) and verifies
/// it against a committed baseline. Any unintended change to the fragment markup — a state class, a badge, an
/// action button, a form field — makes the snapshot drift and the test fail.
///
/// The fixture session carries one line per state/confidence (see <see cref="ReviewSessionFixture"/>); we look
/// each line up by its receipt text so the test reads against the intended state rather than a magic index.
/// </summary>
public sealed class ReviewFragmentSnapshotTests(ReviewFragmentFactory factory)
    : IClassFixture<ReviewFragmentFactory>
{
    private async Task<string> GetPageAsync()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());
        var response = await client.GetAsync($"/Intake/Review/{factory.SessionAId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private Guid LineId(string receiptText) =>
        factory.SessionA.Lines.Single(l => l.ReceiptText == receiptText).Id.Value;

    private async Task VerifyRow(string receiptText, [System.Runtime.CompilerServices.CallerMemberName] string caller = "") =>
        await Verify(FragmentHtml.Row(await GetPageAsync(), LineId(receiptText)), "html")
            .UseMethodName(caller);

    // ── Row states ─────────────────────────────────────────────────────────────────────────────────

    [Fact] // Pending + High → matched (confidence badge + drawer closed)
    public Task Row_matched() => VerifyRow("WHOLE MILK 2L");

    [Fact] // Pending + None → unmatched (no badge, drawer starts open to resolve)
    public Task Row_unmatched() => VerifyRow("MYSTERY ITEM XZ");

    [Fact] // Confirmed via ConfirmAsNew → "· new product"
    public Task Row_new_product() => VerifyRow("ARTISAN SOURDOUGH");

    [Fact] // Dismissed → dimmed, "Add anyway"
    public Task Row_dismissed() => VerifyRow("PLASTIC BAG");

    [Fact] // Committed → locked, "Added", no actions
    public Task Row_committed() => VerifyRow("BUTTER 250G");

    // ── Confidence variants (the badge only renders on a non-unmatched row) ──────────────────────────

    [Fact] // High badge — on the matched (Pending + High) row
    public Task Confidence_high() => VerifyRow("WHOLE MILK 2L");

    [Fact] // Low badge — visible on a Confirmed line carrying Low confidence (the committed line is Low)
    public Task Confidence_low() => VerifyRow("BUTTER 250G");

    [Fact] // None badge — visible on a Confirmed line carrying None confidence (the new-product line is None)
    public Task Confidence_none() => VerifyRow("ARTISAN SOURDOUGH");

    // ── Confirmed-against-existing row (resolved, High badge) ────────────────────────────────────────

    [Fact]
    public Task Row_confirmed_existing() => VerifyRow("FREE RANGE EGGS");

    // ── "Did you mean" alternatives strip ───────────────────────────────────────────────────────────

    [Fact] // Pending + Low + alternatives → match-suggest strip renders before edit-grid
    public Task Row_with_alternatives() => VerifyRow("CHEDDAR BLK 400G");

    // ── Commit / progress bar ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Commit_bar() =>
        await Verify(FragmentHtml.CommitBar(await GetPageAsync()), "html");

    // ── All rows together (ordering + count across every state) ──────────────────────────────────────

    [Fact]
    public async Task All_rows() =>
        await Verify(FragmentHtml.AllRows(await GetPageAsync()), "html");
}
