using System.Net;
using System.Text;
using System.Text.Json;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// Boundary assertions for the review route: it is gated by <c>[Authorize]</c> and tenant-scoped.
///
/// The ADR-013 OOB-bundle contract tests (which asserted HX-Retarget headers and OOB fragment ids)
/// have been retired for this surface as of the 2026-06-22 ADR-013 amendment: the Intake review form
/// now runs on a Preact island that manages aggregates client-side via computed signals, making
/// derived-view drift structurally impossible. The server endpoints return JSON instead of HTML
/// fragments. OobContract + ReviewBoundaryTests assertions are kept for surfaces still on htmx.
///
/// What this file now asserts:
///   1. Auth/tenancy gates (unchanged — the island doesn't affect authorization).
///   2. Validation errors surface as JSON with an error field (still 200 so the island can read the body).
///   3. Successful row actions return JSON with the correct status field.
///   4. Commit/Discard return JSON { redirectUrl }.
/// </summary>
public sealed class ReviewBoundaryTests(ReviewFragmentFactory factory) : IClassFixture<ReviewFragmentFactory>
{
    private string ReviewUrl => $"/Intake/Review/{factory.SessionAId}";

    // ── Auth / tenancy gates (ADR-013 OOB amendment does NOT touch these) ─────────────────────

    [Fact]
    public async Task Unauthenticated_request_is_rejected()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync(ReviewUrl);

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
        // The page now embeds the island hydration JSON rather than rendering an OOB bundle.
        // The hydration script element is the sign the page loaded correctly.
        Assert.Contains("review-island-data", body);
        Assert.Contains("review-island-root", body);
    }

    [Fact]
    public async Task Session_is_not_readable_by_another_household()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdBId.ToString());

        var response = await client.GetAsync(ReviewUrl);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── GET: hydration JSON is well-formed and contains expected session data ──────────────────

    [Fact]
    public async Task Get_page_embeds_valid_hydration_json()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());

        var response = await client.GetAsync(ReviewUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // Extract the hydration JSON from the script element
        var hydrationJson = ExtractHydrationJson(body, "review-island-data");
        Assert.NotNull(hydrationJson);

        var doc = JsonDocument.Parse(hydrationJson!);
        var root = doc.RootElement;

        // Session header
        Assert.True(root.TryGetProperty("merchantText", out _), "Hydration must include merchantText.");
        Assert.True(root.TryGetProperty("today", out _), "Hydration must include today.");

        // Endpoint URLs
        Assert.True(root.TryGetProperty("saveLineUrl", out _), "Hydration must include saveLineUrl.");
        Assert.True(root.TryGetProperty("dismissLineUrl", out _), "Hydration must include dismissLineUrl.");
        Assert.True(root.TryGetProperty("restoreLineUrl", out _), "Hydration must include restoreLineUrl.");
        Assert.True(root.TryGetProperty("commitUrl", out _), "Hydration must include commitUrl.");
        Assert.True(root.TryGetProperty("discardUrl", out _), "Hydration must include discardUrl.");

        // Reference data
        Assert.True(root.TryGetProperty("products", out var products), "Hydration must include products.");
        Assert.True(products.GetArrayLength() > 0, "Hydration products must be non-empty.");
        Assert.True(root.TryGetProperty("units", out _), "Hydration must include units.");
        Assert.True(root.TryGetProperty("locations", out _), "Hydration must include locations.");
        Assert.True(root.TryGetProperty("categories", out _), "Hydration must include categories.");

        // Per-line hydration (8 lines in the fixture)
        Assert.True(root.TryGetProperty("lines", out var lines), "Hydration must include lines.");
        Assert.Equal(8, lines.GetArrayLength());

        // Each line has { line: {...}, prefill: {...} }
        var firstLine = lines[0];
        Assert.True(firstLine.TryGetProperty("line", out var lineData), "Each hydration line must have a 'line' object.");
        Assert.True(firstLine.TryGetProperty("prefill", out var prefill), "Each hydration line must have a 'prefill' object.");
        Assert.True(lineData.TryGetProperty("lineId", out _), "Line data must include lineId.");
        Assert.True(lineData.TryGetProperty("status", out _), "Line data must include status.");
        Assert.True(prefill.TryGetProperty("productId", out _), "Prefill must include productId.");
    }

    [Fact]
    public async Task Hydration_prefill_applies_priority_chain_for_matched_line()
    {
        // The matched line (WHOLE MILK 2L) has High confidence + AI suggestions.
        // The hydration prefill must carry the server-computed values (quantity=2, unit=Litre, location=Fridge).
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());

        var response = await client.GetAsync(ReviewUrl);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        var hydrationJson = ExtractHydrationJson(body, "review-island-data")!;
        var doc = JsonDocument.Parse(hydrationJson);
        var lines = doc.RootElement.GetProperty("lines");

        // Find the WHOLE MILK 2L line (first line, matched/High)
        var milkLine = lines.EnumerateArray()
            .FirstOrDefault(l => l.GetProperty("line").GetProperty("receiptText").GetString() == "WHOLE MILK 2L");
        Assert.True(milkLine.ValueKind != JsonValueKind.Undefined, "Expected WHOLE MILK 2L line in hydration.");

        var prefill = milkLine.GetProperty("prefill");
        // prefill.productId should be the Milk product id
        Assert.Equal(ReviewSessionFixture.MilkProductId.ToString(), prefill.GetProperty("productId").GetString());
        // prefill.quantity should be 2 (AI suggested)
        Assert.Equal(2m, prefill.GetProperty("quantity").GetDecimal());
        // prefill.unitId should be LitreUnitId (from receipt label "L")
        Assert.Equal(ReviewSessionFixture.LitreUnitId.ToString(), prefill.GetProperty("unitId").GetString());
        // prefill.locationId should be FridgeLocationId (product default)
        Assert.Equal(ReviewSessionFixture.FridgeLocationId.ToString(), prefill.GetProperty("locationId").GetString());
    }

    // ── JSON endpoint: SaveLine validation errors ─────────────────────────────────────────────

    [Fact]
    public async Task SaveLine_without_quantity_returns_json_error()
    {
        // Previously: returned 200 HTML with import-row__error (htmx OOB approach).
        // Now: returns 200 JSON { error: "..." } — island reads it and shows inline error.
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());

        var lineId = factory.SessionA.Lines.First().Id.Value;
        var payload = new { lineId, createNew = false, productId = (Guid?)null, quantity = (decimal?)null };
        var json = JsonSerializer.Serialize(payload);

        // Add antiforgery token header (island uses RequestVerificationToken header)
        var pageHtml = await (await client.GetAsync(ReviewUrl)).Content.ReadAsStringAsync();
        var token = AntiforgeryToken(pageHtml);
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/Intake/Review/{factory.SessionAId}?handler=SaveLine");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Add("RequestVerificationToken", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(responseBody);
        Assert.True(doc.RootElement.TryGetProperty("error", out var error), "Response must have 'error' field.");
        Assert.False(string.IsNullOrEmpty(error.GetString()), "Error field must not be empty.");
        Assert.Contains("quantity", error.GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    // ── JSON endpoint: DismissLine ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DismissLine_returns_json_with_dismissed_status()
    {
        using var localFactory = new ReviewFragmentFactory();
        var client = localFactory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());

        var url = $"/Intake/Review/{localFactory.SessionAId}";
        var pageHtml = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var token = AntiforgeryToken(pageHtml);

        var lineId = localFactory.SessionA.Lines.First().Id.Value;

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/Intake/Review/{localFactory.SessionAId}?handler=DismissLine&lineId={lineId}");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        request.Headers.Add("RequestVerificationToken", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(responseBody);
        Assert.True(doc.RootElement.TryGetProperty("status", out var status));
        Assert.Equal("Dismissed", status.GetString());
        Assert.True(doc.RootElement.TryGetProperty("error", out var error));
        Assert.Null(error.GetString());
    }

    // ── JSON endpoint: RestoreLine ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RestoreLine_returns_json_with_pending_status()
    {
        using var localFactory = new ReviewFragmentFactory();
        var client = localFactory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());

        var url = $"/Intake/Review/{localFactory.SessionAId}";
        var pageHtml = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var token = AntiforgeryToken(pageHtml);

        // The fixture has one dismissed line ("PLASTIC BAG").
        var dismissedLine = localFactory.SessionA.Lines.Single(l => l.ReceiptText == "PLASTIC BAG");
        var lineId = dismissedLine.Id.Value;

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/Intake/Review/{localFactory.SessionAId}?handler=RestoreLine&lineId={lineId}");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        request.Headers.Add("RequestVerificationToken", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(responseBody);
        Assert.True(doc.RootElement.TryGetProperty("status", out var status));
        Assert.Equal("Pending", status.GetString());
    }

    // ── JSON endpoint: SaveLine success ──────────────────────────────────────────────────────

    [Fact]
    public async Task SaveLine_with_valid_fields_returns_confirmed_status()
    {
        using var localFactory = new ReviewFragmentFactory();
        var client = localFactory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());

        var url = $"/Intake/Review/{localFactory.SessionAId}";
        var pageHtml = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var token = AntiforgeryToken(pageHtml);

        var lineId = localFactory.SessionA.Lines.First().Id.Value;
        var payload = new
        {
            lineId,
            createNew = false,
            productId = ReviewSessionFixture.MilkProductId,
            skuId = (Guid?)null,
            newProductName = (string?)null,
            newProductCategoryId = (Guid?)null,
            quantity = 2m,
            unitId = ReviewSessionFixture.LitreUnitId,
            locationId = ReviewSessionFixture.FridgeLocationId,
            expiryDate = (string?)null,
            price = (decimal?)null,
        };

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/Intake/Review/{localFactory.SessionAId}?handler=SaveLine");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        request.Headers.Add("RequestVerificationToken", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(responseBody);

        Assert.True(doc.RootElement.TryGetProperty("status", out var status));
        Assert.Equal("Confirmed", status.GetString());

        Assert.True(doc.RootElement.TryGetProperty("error", out var error));
        Assert.Null(error.GetString());
    }

    // ── JSON endpoint: SaveLine server-side validation (server is authoritative, ADR §3) ──────
    // The island mirrors these checks client-side for UX, but the server re-validates every field
    // and is the source of truth. Validation rejections come back as 200 { error } (the island
    // reads the body on non-ok), in the order quantity → unit → location → product/new-product.

    [Fact]
    public async Task SaveLine_missing_unit_returns_choose_a_unit_error()
    {
        using var f = new ReviewFragmentFactory();
        var root = await PostSaveLineAsync(f, new
        {
            lineId = FirstLineId(f), createNew = false, productId = ReviewSessionFixture.MilkProductId,
            quantity = 2m, unitId = (Guid?)null, locationId = ReviewSessionFixture.FridgeLocationId,
        });
        AssertErrorContains(root, "unit");
    }

    [Fact]
    public async Task SaveLine_missing_location_returns_choose_a_location_error()
    {
        using var f = new ReviewFragmentFactory();
        var root = await PostSaveLineAsync(f, new
        {
            lineId = FirstLineId(f), createNew = false, productId = ReviewSessionFixture.MilkProductId,
            quantity = 2m, unitId = ReviewSessionFixture.LitreUnitId, locationId = (Guid?)null,
        });
        AssertErrorContains(root, "location");
    }

    [Fact]
    public async Task SaveLine_existing_without_product_returns_choose_a_product_error()
    {
        using var f = new ReviewFragmentFactory();
        var root = await PostSaveLineAsync(f, new
        {
            lineId = FirstLineId(f), createNew = false, productId = (Guid?)null,
            quantity = 2m, unitId = ReviewSessionFixture.LitreUnitId, locationId = ReviewSessionFixture.FridgeLocationId,
        });
        AssertErrorContains(root, "product");
    }

    [Fact]
    public async Task SaveLine_createNew_without_name_or_category_returns_error()
    {
        using var f = new ReviewFragmentFactory();
        var root = await PostSaveLineAsync(f, new
        {
            lineId = FirstLineId(f), createNew = true, newProductName = (string?)null, newProductCategoryId = (Guid?)null,
            quantity = 1m, unitId = ReviewSessionFixture.EachUnitId, locationId = ReviewSessionFixture.FridgeLocationId,
        });
        AssertErrorContains(root, "category");
    }

    [Fact]
    public async Task SaveLine_createNew_valid_confirms_as_new_product()
    {
        // Exercises the ConfirmLineAsNewCommand path (the existing success test only covers ResolveLine).
        using var f = new ReviewFragmentFactory();
        var root = await PostSaveLineAsync(f, new
        {
            lineId = FirstLineId(f), createNew = true,
            productId = (Guid?)null, skuId = (Guid?)null,
            newProductName = "Brand New Item", newProductCategoryId = ReviewSessionFixture.DairyCategoryId,
            quantity = 1m, unitId = ReviewSessionFixture.EachUnitId, locationId = ReviewSessionFixture.FridgeLocationId,
            expiryDate = (string?)null, price = (decimal?)null,
        });
        Assert.Equal("Confirmed", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("isNewProduct").GetBoolean());
        Assert.Equal("Brand New Item", root.GetProperty("newProductName").GetString());
        Assert.Null(root.GetProperty("error").GetString());
    }

    // ── JSON endpoint: Commit returns redirectUrl ─────────────────────────────────────────────
    // Note: the Commit happy path is covered at the INTEGRATION layer (IntakeCommitTests) — it
    // writes products/stock/prices across bounded contexts and needs a real DB, which the WAF
    // review factory deliberately fakes. A WAF commit test fails on DB connect; not duplicated here.

    [Fact]
    public async Task Discard_returns_json_with_redirect_url()
    {
        using var localFactory = new ReviewFragmentFactory();
        var client = localFactory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());

        var url = $"/Intake/Review/{localFactory.SessionAId}";
        var pageHtml = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var token = AntiforgeryToken(pageHtml);

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/Intake/Review/{localFactory.SessionAId}?handler=Discard");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        request.Headers.Add("RequestVerificationToken", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(responseBody);

        Assert.True(doc.RootElement.TryGetProperty("redirectUrl", out var redirectUrl));
        Assert.False(string.IsNullOrEmpty(redirectUrl.GetString()), "Discard must return a redirectUrl.");
        Assert.Contains("/Pantry", redirectUrl.GetString()!);
    }

    // ── JSON endpoint: ConfirmLines (plantry-kr9h) ────────────────────────────────────────────

    [Fact]
    public async Task ConfirmLines_returns_json_with_confirmed_ids_and_status()
    {
        using var localFactory = new ReviewFragmentFactory();
        var client = localFactory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());

        var url = $"/Intake/Review/{localFactory.SessionAId}";
        var pageHtml = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var token = AntiforgeryToken(pageHtml);

        // WHOLE MILK 2L is Pending, High-confidence, with a complete server-side prefill — it qualifies.
        var milkLine = localFactory.SessionA.Lines.Single(l => l.ReceiptText == "WHOLE MILK 2L");
        var payload = new { lineIds = new[] { milkLine.Id.Value } };

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/Intake/Review/{localFactory.SessionAId}?handler=ConfirmLines");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        request.Headers.Add("RequestVerificationToken", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("Confirmed", root.GetProperty("status").GetString());
        var confirmedIds = root.GetProperty("confirmedLineIds").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { milkLine.Id.Value.ToString() }, confirmedIds);
        Assert.Null(root.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ConfirmLines_with_a_non_qualifying_id_returns_json_error_and_confirms_nothing()
    {
        using var localFactory = new ReviewFragmentFactory();
        var client = localFactory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());

        var url = $"/Intake/Review/{localFactory.SessionAId}";
        var pageHtml = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var token = AntiforgeryToken(pageHtml);

        // The dismissed line ("PLASTIC BAG") is not confirmable; the whole call must fail as JSON.
        var dismissedLine = localFactory.SessionA.Lines.Single(l => l.ReceiptText == "PLASTIC BAG");
        var payload = new { lineIds = new[] { dismissedLine.Id.Value } };

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/Intake/Review/{localFactory.SessionAId}?handler=ConfirmLines");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        request.Headers.Add("RequestVerificationToken", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        AssertErrorContains(root, "PLASTIC BAG");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static Guid FirstLineId(ReviewFragmentFactory f) => f.SessionA.Lines.First().Id.Value;

    /// <summary>GETs the page for a token, POSTs a SaveLine JSON body, asserts 200, returns the parsed root.</summary>
    private static async Task<JsonElement> PostSaveLineAsync(ReviewFragmentFactory factory, object payload)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());
        var pageHtml = await (await client.GetAsync($"/Intake/Review/{factory.SessionAId}")).Content.ReadAsStringAsync();
        var token = AntiforgeryToken(pageHtml);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/Intake/Review/{factory.SessionAId}?handler=SaveLine");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        request.Headers.Add("RequestVerificationToken", token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static void AssertErrorContains(JsonElement root, string fragment)
    {
        Assert.True(root.TryGetProperty("error", out var error), "Response must carry an 'error' field.");
        var msg = error.GetString();
        Assert.False(string.IsNullOrEmpty(msg), "Error message must not be empty.");
        Assert.Contains(fragment, msg!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Pulls the antiforgery request token out of a rendered page.</summary>
    private static string AntiforgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the review page.");
        return match.Groups[1].Value;
    }

    /// <summary>Extracts JSON from a &lt;script type="application/json" id="..."&gt; element.</summary>
    private static string? ExtractHydrationJson(string html, string elementId)
    {
        var pattern = $"<script type=\"application/json\" id=\"{elementId}\">(.*?)</script>";
        var match = System.Text.RegularExpressions.Regex.Match(html, pattern, System.Text.RegularExpressions.RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}
