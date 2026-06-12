using System.Net;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// Boundary assertions for the review route: it is gated by <c>[Authorize]</c> and tenant-scoped.
/// Also asserts the ADR-013 OOB-bundle contract: every successful row action must return
/// HX-Retarget pointing at the changed row plus OOB fragment ids in the body.
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
        // A row-scoped error retargets htmx to the single row (outerHTML) — without this header
        // the error markup would replace the wrong target or not swap at all.
        Assert.True(response.Headers.TryGetValues("HX-Retarget", out var retarget) && retarget.Single() == $"#import-line-{lineId}",
            "RowError must retarget htmx to the offending row.");
    }

    // ── ADR-013 OOB-bundle contract tests ──────────────────────────────────────────────────────────
    // Every successful row action must return: HX-Retarget → #import-line-{id} (outerHTML swap) PLUS
    // OOB fragments for all four aggregate regions (#rev-chips, #rev-progress, #commit-bar, #rcpt-total).
    // No full #rev-body innerHTML repaint on any successful action.

    [Fact]
    public async Task Quick_confirm_returns_oob_bundle_not_full_repaint()
    {
        // ADR-013 §1 contract: a successful quick-confirm carries HX-Retarget on the response header
        // (pointing at the one changed row) plus OOB fragment ids in the body for all four aggregates.
        // The response must NOT contain a full #rev-body innerHTML repaint.
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

        // ADR-013 §1 — HX-Retarget must point at the one changed row.
        Assert.True(
            response.Headers.TryGetValues("HX-Retarget", out var retarget) &&
            retarget.Single() == $"#import-line-{lineId}",
            "Successful row action must HX-Retarget to the changed row.");
        Assert.True(
            response.Headers.TryGetValues("HX-Reswap", out var reswap) &&
            reswap.Single() == "outerHTML",
            "Successful row action must HX-Reswap outerHTML.");

        // ADR-013 §1 — four OOB fragment ids must be in the body.
        Assert.Contains("id=\"rev-chips\"", body);
        Assert.Contains("id=\"rev-progress\"", body);
        Assert.Contains("id=\"commit-bar\"", body);
        Assert.Contains("id=\"rcpt-total\"", body);

        // The OOB chips fragment carries updated counts.
        Assert.Contains("Needs review 2", body);
        Assert.Contains("Ready 4", body);

        // The confirmed row is rendered as confirmed.
        Assert.Contains("import-row--confirmed", body);
        Assert.Contains("import-row__confirmed-flag", body);

        // Guard: NO full #rev-body innerHTML repaint (the old approach, now retired).
        Assert.DoesNotContain("id=\"rev-body\"", body);

        // ADR-013 amendment — the #import-line-{id} element must be the root of the primary body
        // (no outer x-show wrapper). If the outer wrapper were present, htmx would nest a fresh
        // wrapper inside the existing one on every swap, accumulating divs with each row action.
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(body);
        var rowEl = doc.GetElementById($"import-line-{lineId}");
        Assert.NotNull(rowEl);
        Assert.True(
            rowEl!.ParentElement?.TagName.ToLowerInvariant() == "body",
            $"#import-line-{lineId} must be a direct child of <body> in the bundle fragment (no outer wrapper). " +
            $"Actual parent tag: {rowEl.ParentElement?.TagName}.");
    }

    [Fact]
    public async Task Second_action_on_same_row_has_no_nested_wrapper()
    {
        // ADR-013 amendment guard: two consecutive successful actions on the same row must each
        // produce a response with exactly one #import-line-{id} and no outer x-show wrapper.
        // If the outer wrapper existed, the second action would see the wrapper element as the
        // htmx swap root and inject a nested wrapper, leaving two #import-line-{id} elements in the DOM.
        using var localFactory = new ReviewFragmentFactory();
        var client = localFactory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());

        var url = $"/Intake/Review/{localFactory.SessionAId}";
        var pageHtml = await (await client.GetAsync(url)).Content.ReadAsStringAsync();

        var lineId = localFactory.SessionA.Lines.First().Id.Value;

        // First action: confirm the row.
        var confirmForm = new FormUrlEncodedContent(new Dictionary<string, string>
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
        var firstResponse = await client.PostAsync($"{url}?handler=SaveLine", confirmForm);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        // Re-fetch antiforgery token for the second action.
        pageHtml = await (await client.GetAsync(url)).Content.ReadAsStringAsync();

        // Second action: dismiss the same row.
        var dismissForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryToken(pageHtml),
            ["Id"] = localFactory.SessionAId.ToString(),
        });
        var secondResponse = await client.PostAsync($"{url}?handler=DismissLine&lineId={lineId}", dismissForm);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var body2 = await secondResponse.Content.ReadAsStringAsync();

        // The second response must contain exactly one occurrence of #import-line-{lineId}.
        var occurrences = System.Text.RegularExpressions.Regex.Matches(body2, $"id=\"import-line-{lineId}\"").Count;
        Assert.True(
            occurrences == 1,
            $"Expected exactly one #import-line-{lineId} in the second action's response body, found {occurrences}. " +
            $"Multiple occurrences indicate an outer wrapper is being injected on each swap.");

        // Root element of the bundle's primary body must be #import-line-{lineId} (no outer wrapper).
        var parser = new HtmlParser();
        var doc2 = parser.ParseDocument(body2);
        var rowEl2 = doc2.GetElementById($"import-line-{lineId}");
        Assert.NotNull(rowEl2);
        Assert.True(
            rowEl2!.ParentElement?.TagName.ToLowerInvariant() == "body",
            $"#import-line-{lineId} must be a direct child of <body> in the bundle fragment (no outer wrapper).");
    }

    [Fact]
    public async Task Dismiss_line_returns_oob_bundle()
    {
        // ADR-013 §1 — dismiss must also return the OOB-bundle contract.
        using var localFactory = new ReviewFragmentFactory();
        var client = localFactory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());

        var url = $"/Intake/Review/{localFactory.SessionAId}";
        var pageHtml = await (await client.GetAsync(url)).Content.ReadAsStringAsync();

        // Dismiss the first pending line (WHOLE MILK 2L).
        var lineId = localFactory.SessionA.Lines.First().Id.Value;
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryToken(pageHtml),
            ["Id"] = localFactory.SessionAId.ToString(),
        });

        var response = await client.PostAsync($"{url}?handler=DismissLine&lineId={lineId}", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // HX-Retarget must point at the dismissed row.
        Assert.True(
            response.Headers.TryGetValues("HX-Retarget", out var retarget) &&
            retarget.Single() == $"#import-line-{lineId}",
            "Dismiss must HX-Retarget to the dismissed row.");

        // OOB fragments present.
        Assert.Contains("id=\"rev-chips\"", body);
        Assert.Contains("id=\"rev-progress\"", body);
        Assert.Contains("id=\"commit-bar\"", body);
        Assert.Contains("id=\"rcpt-total\"", body);

        // Row is now dismissed.
        Assert.Contains("import-row--dismissed", body);
    }

    [Fact]
    public async Task Restore_line_returns_oob_bundle()
    {
        // ADR-013 §1 — restore must also return the OOB-bundle contract.
        using var localFactory = new ReviewFragmentFactory();
        var client = localFactory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());

        var url = $"/Intake/Review/{localFactory.SessionAId}";
        var pageHtml = await (await client.GetAsync(url)).Content.ReadAsStringAsync();

        // The fixture has one dismissed line ("PLASTIC BAG").
        var dismissedLine = localFactory.SessionA.Lines.Single(l => l.ReceiptText == "PLASTIC BAG");
        var lineId = dismissedLine.Id.Value;
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryToken(pageHtml),
            ["Id"] = localFactory.SessionAId.ToString(),
        });

        var response = await client.PostAsync($"{url}?handler=RestoreLine&lineId={lineId}", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // HX-Retarget must point at the restored row.
        Assert.True(
            response.Headers.TryGetValues("HX-Retarget", out var retarget) &&
            retarget.Single() == $"#import-line-{lineId}",
            "Restore must HX-Retarget to the restored row.");

        // OOB fragments present.
        Assert.Contains("id=\"rev-chips\"", body);
        Assert.Contains("id=\"rev-progress\"", body);
        Assert.Contains("id=\"commit-bar\"", body);
        Assert.Contains("id=\"rcpt-total\"", body);

        // Restored row is no longer dismissed.
        Assert.DoesNotContain("import-row--dismissed", body);
    }

    [Fact]
    public async Task Drawer_confirm_existing_product_returns_oob_bundle()
    {
        // ADR-013 §1 — the edit-drawer confirm (use-existing path) must return the OOB-bundle contract.
        using var localFactory = new ReviewFragmentFactory();
        var client = localFactory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());

        var url = $"/Intake/Review/{localFactory.SessionAId}";
        var pageHtml = await (await client.GetAsync(url)).Content.ReadAsStringAsync();

        // Confirm MYSTERY ITEM XZ (unmatched, no quick-confirm; goes through the drawer form).
        var lineId = localFactory.SessionA.Lines.Single(l => l.ReceiptText == "MYSTERY ITEM XZ").Id.Value;
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryToken(pageHtml),
            ["Id"] = localFactory.SessionAId.ToString(),
            ["Edit.LineId"] = lineId.ToString(),
            ["Edit.CreateNew"] = "false",
            ["Edit.ProductId"] = ReviewSessionFixture.BreadProductId.ToString(),
            ["Edit.Quantity"] = "1",
            ["Edit.UnitId"] = ReviewSessionFixture.EachUnitId.ToString(),
            ["Edit.LocationId"] = ReviewSessionFixture.FridgeLocationId.ToString(),
        });

        var response = await client.PostAsync($"{url}?handler=SaveLine", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.Headers.TryGetValues("HX-Retarget", out var retarget) &&
            retarget.Single() == $"#import-line-{lineId}",
            "Drawer confirm (existing) must HX-Retarget to the changed row.");

        Assert.Contains("id=\"rev-chips\"", body);
        Assert.Contains("id=\"rev-progress\"", body);
        Assert.Contains("id=\"commit-bar\"", body);
        Assert.Contains("id=\"rcpt-total\"", body);
        Assert.Contains("import-row--confirmed", body);
    }

    [Fact]
    public async Task Drawer_confirm_create_new_returns_oob_bundle()
    {
        // ADR-013 §1 — the edit-drawer confirm (create-new path) must return the OOB-bundle contract.
        using var localFactory = new ReviewFragmentFactory();
        var client = localFactory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());

        var url = $"/Intake/Review/{localFactory.SessionAId}";
        var pageHtml = await (await client.GetAsync(url)).Content.ReadAsStringAsync();

        // Confirm ORG BREAD LOAF (low-confidence, goes through the drawer form) as a new product.
        var lineId = localFactory.SessionA.Lines.Single(l => l.ReceiptText == "ORG BREAD LOAF").Id.Value;
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryToken(pageHtml),
            ["Id"] = localFactory.SessionAId.ToString(),
            ["Edit.LineId"] = lineId.ToString(),
            ["Edit.CreateNew"] = "true",
            ["Edit.NewProductName"] = "Organic Bread Loaf",
            ["Edit.NewProductCategoryId"] = ReviewSessionFixture.DairyCategoryId.ToString(),
            ["Edit.Quantity"] = "1",
            ["Edit.UnitId"] = ReviewSessionFixture.EachUnitId.ToString(),
            ["Edit.LocationId"] = ReviewSessionFixture.FridgeLocationId.ToString(),
        });

        var response = await client.PostAsync($"{url}?handler=SaveLine", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.Headers.TryGetValues("HX-Retarget", out var retarget) &&
            retarget.Single() == $"#import-line-{lineId}",
            "Drawer confirm (create-new) must HX-Retarget to the changed row.");

        Assert.Contains("id=\"rev-chips\"", body);
        Assert.Contains("id=\"rev-progress\"", body);
        Assert.Contains("id=\"commit-bar\"", body);
        Assert.Contains("id=\"rcpt-total\"", body);
        Assert.Contains("import-row--confirmed", body);
        Assert.Contains("new product", body);
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
