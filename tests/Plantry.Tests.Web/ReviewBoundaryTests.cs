using System.Net;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// Boundary assertions for the review route: it is gated by <c>[Authorize]</c> and tenant-scoped. These do not
/// snapshot markup — they assert the security envelope around the fragments.
/// </summary>
public sealed class ReviewBoundaryTests(ReviewFragmentFactory factory) : IClassFixture<ReviewFragmentFactory>
{
    private string ReviewUrl => $"/Intake/Review/{factory.SessionAId}";

    [Fact]
    public async Task Unauthenticated_request_is_rejected()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync(ReviewUrl);

        // No household header → not authenticated → [Authorize] challenges. The test scheme issues a bare 401
        // (a cookie scheme would 302 to the login path); either is an "unauthenticated is blocked" boundary.
        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Found or HttpStatusCode.Redirect,
            $"Expected 401/redirect for an unauthenticated request, got {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task Household_that_owns_the_session_can_read_it()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());

        var response = await client.GetAsync(ReviewUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Review import", body);
    }

    [Fact]
    public async Task Session_is_not_readable_by_another_household()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        // Household B authenticates, but the session belongs to household A.
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdBId.ToString());

        var response = await client.GetAsync(ReviewUrl);

        // The tenant-scoped repository returns null for a foreign household → the query maps to NotFound.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_line_edit_returns_200_so_htmx_swaps_the_inline_error()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());

        // Razor Pages auto-validates antiforgery on POST, so GET the page first to capture a matching token
        // (the paired antiforgery cookie rides along on the client's shared cookie container).
        var pageHtml = await (await client.GetAsync(ReviewUrl)).Content.ReadAsStringAsync();
        var token = AntiforgeryToken(pageHtml);

        // A line edit that omits the user-resolved quantity. The handler rejects it with an inline row error.
        // Regression guard: this MUST come back 200, not 422 — htmx 2.x refuses to swap a 4xx by default, so
        // RowError/CommitBarError deliberately return 200 carrying the error markup (Review.cshtml.cs). If a
        // future change restores the 422, the error banner silently stops appearing in the UI; this pins it.
        var lineId = factory.SessionA.Lines.First().Id.Value; // line 1 — still Pending, so the row re-renders
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Id"] = factory.SessionAId.ToString(),
            ["Edit.LineId"] = lineId.ToString(),
            ["Edit.CreateNew"] = "false",
            // Edit.Quantity intentionally omitted → "Enter a quantity greater than zero."
        });

        var response = await client.PostAsync($"{ReviewUrl}?handler=SaveLine", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Enter a quantity greater than zero.", body);
        Assert.Contains("import-row__error", body);
        // The forms target #rev-body, so a row-scoped error retargets htmx back to the single row (outerHTML);
        // without this header the error markup would replace the whole list with one row.
        Assert.True(response.Headers.TryGetValues("HX-Retarget", out var retarget) && retarget.Single() == $"#import-line-{lineId}",
            "RowError must retarget htmx to the offending row.");
    }

    [Fact]
    public async Task Confirming_a_line_re_renders_the_whole_body_with_updated_buckets()
    {
        // A successful row action re-renders #rev-body (header chips + grouped sections) as one unit, so the
        // "Needs review"/"Ready" chip counts and the row's section membership move in lock-step with the
        // progress bar. This pins the redesign's core invariant: before the fix only the progress bar + commit
        // bar were OOB-refreshed, leaving the chips and buckets stale after every action.
        // Own factory so the in-memory session mutation (line 1 → Confirmed) can't leak into sibling tests.
        using var localFactory = new ReviewFragmentFactory();
        var client = localFactory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());

        var url = $"/Intake/Review/{localFactory.SessionAId}";
        var pageHtml = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        // Fixture starts with 3 Pending lines (needs review) and 3 resolved (ready).
        Assert.Contains("Needs review 3", pageHtml);
        Assert.Contains("Ready 3", pageHtml);

        // Confirm line 1 ("WHOLE MILK 2L") against its matched product with valid user-resolved fields.
        var lineId = localFactory.SessionA.Lines.First().Id.Value;
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryToken(pageHtml),
            ["Id"] = localFactory.SessionAId.ToString(),
            ["Edit.LineId"] = lineId.ToString(),
            ["Edit.CreateNew"] = "false",
            ["Edit.ProductId"] = ReviewSessionFixture.MilkProductId.ToString(),
            ["Edit.Quantity"] = "2",
            ["Edit.UnitId"] = ReviewSessionFixture.LitreUnitId.ToString(),
            ["Edit.LocationId"] = ReviewSessionFixture.FridgeLocationId.ToString(),
        });

        var response = await client.PostAsync($"{url}?handler=SaveLine", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // Counts moved together — one fewer to review, one more ready.
        Assert.Contains("Needs review 2", body);
        Assert.Contains("Ready 4", body);
        // The confirmed row is now rendered in its new "Matched & ready" bucket…
        Assert.Contains("Matched &amp; ready", body);
        Assert.Contains("import-row__confirmed-flag", body);
        // …and the response carries the out-of-band commit-bar refresh (it lives outside #rev-body).
        Assert.Contains("hx-swap-oob", body);
    }

    /// <summary>Pulls the antiforgery request token out of a rendered page (the hidden field that
    /// <c>@Html.AntiForgeryToken()</c> emits) so a test POST can satisfy Razor Pages' auto-validation.</summary>
    private static string AntiforgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the review page.");
        return match.Groups[1].Value;
    }
}
