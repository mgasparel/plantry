using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Intake;

/// <summary>
/// L4 tests for <c>/Intake/Manual</c> (plantry-45ba.3) — the typed-purchase form. Drives the same
/// JS-independent POST backstop as <c>RecipeEditorInlineCreateBouncePostTests</c>: the Alpine-rendered
/// hidden <c>Input.Lines[n].*</c> inputs are posted directly, exercising the real server-side model
/// binding and <see cref="Plantry.Intake.Application.LogManualPurchaseCommand"/> wiring end to end
/// against the in-memory fakes in <see cref="ManualPurchaseFormFactory"/> — no database is touched.
///
/// <para>Multi-line commit / blank-price / inline-create BUSINESS RULES are already exhaustively
/// unit-tested at the command level (plantry-45ba.2,
/// <c>Plantry.Tests.Unit.Intake.Application.LogManualPurchaseCommandTests</c>). These tests instead
/// prove the PAGE's own responsibility: that a real HTTP POST of the form's fields reaches the command
/// with the right inputs and redirects on success, and that a validation bounce re-renders the form
/// with the user's typed lines intact (the acceptance criterion called out as the main failure mode
/// worth testing) rather than losing them.</para>
/// </summary>
public sealed class ManualIntakePageTests : IDisposable
{
    private readonly ManualPurchaseFormFactory _factory = new();
    private static readonly HtmlParser Parser = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient AuthClient() => _factory.AuthClient();

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var html = await (await client.GetAsync("/Intake/Manual")).Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the Manual intake page.");
        return match.Groups[1].Value;
    }

    // ── GET ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_renders_the_manual_intake_form()
    {
        var resp = await AuthClient().GetAsync("/Intake/Manual");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Contains("Enter a purchase", html);
        Assert.Contains("Purchase date", html);
        Assert.Contains("Log purchase", html);

        // Reference-data wiring — not just page chrome. Each select must actually carry the
        // fixture's options: the location select (fed via ViewData["ManualLocationOptions"] into the
        // ExtraFieldsPartial slot), and the shared sheet's create-view unit + category selects.
        var doc = Parser.ParseDocument(html);

        var locationOptions = doc.QuerySelectorAll("#manual-line-location option")
            .Select(o => o.GetAttribute("value")).ToList();
        Assert.Contains(ReviewSessionFixture.FridgeLocationId.ToString(), locationOptions);

        var unitOptions = doc.QuerySelectorAll("#create-product-unit option")
            .Select(o => o.GetAttribute("value")).ToList();
        Assert.Contains(ReviewSessionFixture.LitreUnitId.ToString(), unitOptions);

        var categoryOptions = doc.QuerySelectorAll("#create-product-category option")
            .Select(o => o.GetAttribute("value")).ToList();
        Assert.Contains(ReviewSessionFixture.DairyCategoryId.ToString(), categoryOptions);

        // The store combobox is fed from the serialised store list in the form's x-data, not a
        // <select> — assert the household's stores actually reach it.
        var form = doc.QuerySelector("form[x-data^='manualPurchaseForm']")
            ?? throw new InvalidOperationException("Manual purchase form not found.");
        var xData = form.GetAttribute("x-data")!;
        Assert.Contains($"\"id\":\"{ReviewSessionFixture.FreshMartStoreId}\"", xData);
        Assert.Contains("\"name\":\"Fresh Mart\"", xData);
    }

    [Fact]
    public async Task Get_threads_the_household_display_currency_into_the_form()
    {
        // GBP household → the Alpine factory call's final argument is the pound symbol, so the
        // client-side rowSummary() renders prices in the household's currency, not a hardcoded "$".
        _factory.DisplayCurrency = "GBP";
        var resp = await AuthClient().GetAsync("/Intake/Manual");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        var doc = Parser.ParseDocument(html);
        var form = doc.QuerySelector("form[x-data^='manualPurchaseForm']")
            ?? throw new InvalidOperationException("Manual purchase form not found.");
        var xData = form.GetAttribute("x-data")!;
        Assert.EndsWith(", '£')", xData);
    }

    [Fact]
    public async Task Unauthenticated_get_is_challenged()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var resp = await client.GetAsync("/Intake/Manual");

        Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode);
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Single_item_purchase_commits_and_redirects_to_the_session_detail()
    {
        var client = AuthClient();
        var token = await GetAntiforgeryTokenAsync(client);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.PurchaseDate", "2026-07-30"),
            new("Input.Lines[0].ProductId", ReviewSessionFixture.MilkProductId.ToString()),
            new("Input.Lines[0].Quantity", "2"),
            new("Input.Lines[0].UnitId", ReviewSessionFixture.LitreUnitId.ToString()),
            new("Input.Lines[0].LocationId", ReviewSessionFixture.FridgeLocationId.ToString()),
            new("Input.Lines[0].Price", "3.99"),
        };

        var resp = await client.PostAsync("/Intake/Manual", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/Intake/Session/", resp.Headers.Location!.OriginalString);

        var session = Assert.Single(_factory.Sessions.Sessions);
        Assert.Equal(Plantry.Intake.Domain.ImportSourceType.Manual, session.SourceType);
        Assert.Equal(Plantry.Intake.Domain.ImportStatus.Committed, session.Status);
        Assert.Equal(ReviewSessionFixture.MilkProductId, Assert.Single(_factory.AddStock.ProductIds));
        Assert.Equal(3.99m, Assert.Single(_factory.RecordPrice.Prices));
    }

    [Fact]
    public async Task Ten_line_shop_commits_all_lines_in_one_submit()
    {
        var client = AuthClient();
        var token = await GetAntiforgeryTokenAsync(client);

        var fields = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token), new("Input.PurchaseDate", "2026-07-30") };
        for (var i = 0; i < 10; i++)
        {
            fields.Add(new($"Input.Lines[{i}].ProductId", ReviewSessionFixture.MilkProductId.ToString()));
            fields.Add(new($"Input.Lines[{i}].Quantity", "1"));
            fields.Add(new($"Input.Lines[{i}].UnitId", ReviewSessionFixture.LitreUnitId.ToString()));
            fields.Add(new($"Input.Lines[{i}].LocationId", ReviewSessionFixture.FridgeLocationId.ToString()));
            fields.Add(new($"Input.Lines[{i}].Price", "1.00"));
        }

        var resp = await client.PostAsync("/Intake/Manual", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal(10, _factory.AddStock.ProductIds.Count);
        Assert.Equal(10, _factory.RecordPrice.Prices.Count);
        var session = Assert.Single(_factory.Sessions.Sessions);
        Assert.Equal(10, session.Lines.Count);
    }

    [Fact]
    public async Task Blank_price_line_commits_stock_only()
    {
        var client = AuthClient();
        var token = await GetAntiforgeryTokenAsync(client);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.PurchaseDate", "2026-07-30"),
            // Line 1: priced.
            new("Input.Lines[0].ProductId", ReviewSessionFixture.MilkProductId.ToString()),
            new("Input.Lines[0].Quantity", "1"),
            new("Input.Lines[0].UnitId", ReviewSessionFixture.LitreUnitId.ToString()),
            new("Input.Lines[0].LocationId", ReviewSessionFixture.FridgeLocationId.ToString()),
            new("Input.Lines[0].Price", "2.99"),
            // Line 2: no price — the "lost receipt, don't remember this one" case.
            new("Input.Lines[1].ProductId", ReviewSessionFixture.BreadProductId.ToString()),
            new("Input.Lines[1].Quantity", "1"),
            new("Input.Lines[1].UnitId", ReviewSessionFixture.EachUnitId.ToString()),
            new("Input.Lines[1].LocationId", ReviewSessionFixture.FridgeLocationId.ToString()),
        };

        var resp = await client.PostAsync("/Intake/Manual", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal(2, _factory.AddStock.ProductIds.Count); // both lines stocked
        Assert.Equal(2.99m, Assert.Single(_factory.RecordPrice.Prices)); // only the priced line wrote an observation
    }

    [Fact]
    public async Task Inline_new_product_create_works_mid_form()
    {
        var client = AuthClient();
        var token = await GetAntiforgeryTokenAsync(client);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.PurchaseDate", "2026-07-30"),
            // Line 1: an existing product.
            new("Input.Lines[0].ProductId", ReviewSessionFixture.MilkProductId.ToString()),
            new("Input.Lines[0].Quantity", "1"),
            new("Input.Lines[0].UnitId", ReviewSessionFixture.LitreUnitId.ToString()),
            new("Input.Lines[0].LocationId", ReviewSessionFixture.FridgeLocationId.ToString()),
            // Line 2: created inline, mid-form.
            new("Input.Lines[1].NewProductName", "Artisan sourdough"),
            new("Input.Lines[1].NewProductCategoryId", ReviewSessionFixture.DairyCategoryId.ToString()),
            new("Input.Lines[1].Quantity", "1"),
            new("Input.Lines[1].UnitId", ReviewSessionFixture.EachUnitId.ToString()),
            new("Input.Lines[1].LocationId", ReviewSessionFixture.FridgeLocationId.ToString()),
            new("Input.Lines[1].Price", "6.00"),
        };

        var resp = await client.PostAsync("/Intake/Manual", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var created = Assert.Single(_factory.CreateProduct.Calls);
        Assert.Equal("Artisan sourdough", created.Name);
        Assert.Equal(ReviewSessionFixture.DairyCategoryId, created.CategoryId);
        Assert.Equal(2, _factory.AddStock.ProductIds.Count);
    }

    [Fact]
    public async Task Typed_store_name_finds_or_creates_the_store()
    {
        var client = AuthClient();
        var token = await GetAntiforgeryTokenAsync(client);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.PurchaseDate", "2026-07-30"),
            new("Input.MerchantText", "Corner Store"),
            new("Input.Lines[0].ProductId", ReviewSessionFixture.MilkProductId.ToString()),
            new("Input.Lines[0].Quantity", "1"),
            new("Input.Lines[0].UnitId", ReviewSessionFixture.LitreUnitId.ToString()),
            new("Input.Lines[0].LocationId", ReviewSessionFixture.FridgeLocationId.ToString()),
            new("Input.Lines[0].Price", "3.99"),
        };

        var resp = await client.PostAsync("/Intake/Manual", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("Corner Store", Assert.Single(_factory.EnsureStore.Calls));
    }

    [Fact]
    public async Task Picked_store_id_commits_with_no_name_find_or_create()
    {
        var client = AuthClient();
        var token = await GetAntiforgeryTokenAsync(client);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.PurchaseDate", "2026-07-30"),
            new("Input.MerchantText", "Fresh Mart"),
            new("Input.SelectedStoreId", ReviewSessionFixture.FreshMartStoreId.ToString()),
            new("Input.Lines[0].ProductId", ReviewSessionFixture.MilkProductId.ToString()),
            new("Input.Lines[0].Quantity", "1"),
            new("Input.Lines[0].UnitId", ReviewSessionFixture.LitreUnitId.ToString()),
            new("Input.Lines[0].LocationId", ReviewSessionFixture.FridgeLocationId.ToString()),
            new("Input.Lines[0].Price", "3.99"),
        };

        var resp = await client.PostAsync("/Intake/Manual", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Empty(_factory.EnsureStore.Calls); // picked store id used directly, no name round-trip
    }

    // ── Product search (server-computed prefill, ReviewPrefill) ─────────────────

    [Fact]
    public async Task Search_hit_carries_the_server_computed_expiry_prefill()
    {
        var client = AuthClient();

        var resp = await client.GetAsync("/Intake/Manual?handler=SearchProducts&q=Milk");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        // MilkProductId's DefaultDueDays is 7; the factory pins SnapshotFixedClock to SnapshotDate
        // (2026-06-15), so the server-computed prefill is deterministic — 2026-06-22.
        Assert.Contains("data-default-expiry=\"2026-06-22\"", html);
    }

    // ── Validation bounce (acceptance: don't lose a half-typed shop) ────────────

    [Fact]
    public async Task Zero_lines_bounces_with_no_session_started()
    {
        var client = AuthClient();
        var token = await GetAntiforgeryTokenAsync(client);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.PurchaseDate", "2026-07-30"),
        };

        var resp = await client.PostAsync("/Intake/Manual", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // re-rendered, not redirected
        Assert.Empty(_factory.Sessions.Sessions);
    }

    [Fact]
    public async Task Omitted_purchase_date_bounces_with_no_session_started()
    {
        var client = AuthClient();
        var token = await GetAntiforgeryTokenAsync(client);

        // Input.PurchaseDate key omitted entirely (not merely blank — model binding for a bad-but-present
        // value already fails ModelState on its own) — this is the guard's real trigger: an omitted key
        // leaves the non-nullable DateOnly at its bind default (0001-01-01) with no ModelState error.
        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.Lines[0].ProductId", ReviewSessionFixture.MilkProductId.ToString()),
            new("Input.Lines[0].Quantity", "1"),
            new("Input.Lines[0].UnitId", ReviewSessionFixture.LitreUnitId.ToString()),
            new("Input.Lines[0].LocationId", ReviewSessionFixture.FridgeLocationId.ToString()),
        };

        var resp = await client.PostAsync("/Intake/Manual", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // re-rendered, not redirected
        Assert.Empty(_factory.Sessions.Sessions);
    }

    [Fact]
    public async Task Line_missing_a_location_bounces_and_reseeds_the_typed_store_intact()
    {
        var client = AuthClient();
        var token = await GetAntiforgeryTokenAsync(client);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.PurchaseDate", "2026-07-30"),
            new("Input.MerchantText", "Fresh Mart"),
            new("Input.SelectedStoreId", ReviewSessionFixture.FreshMartStoreId.ToString()),
            new("Input.Lines[0].ProductId", ReviewSessionFixture.MilkProductId.ToString()),
            new("Input.Lines[0].Quantity", "2"),
            new("Input.Lines[0].UnitId", ReviewSessionFixture.LitreUnitId.ToString()),
            new("Input.Lines[0].Price", "3.99"),
            // LocationId omitted — the guard this test targets.
        };

        var resp = await client.PostAsync("/Intake/Manual", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty(_factory.Sessions.Sessions);

        var body = await resp.Content.ReadAsStringAsync();
        var doc = Parser.ParseDocument(body);
        var form = doc.QuerySelector("form[x-data^='manualPurchaseForm']")!;
        var xData = form.GetAttribute("x-data")!;

        // The header — typed store name + picked store id — survives the bounce alongside the lines.
        Assert.Contains("\"storeQuery\":\"Fresh Mart\"", xData);
        Assert.Contains($"\"selectedStoreId\":\"{ReviewSessionFixture.FreshMartStoreId}\"", xData);
    }

    [Fact]
    public async Task Line_missing_a_location_bounces_and_reseeds_the_typed_line_intact()
    {
        var client = AuthClient();
        var token = await GetAntiforgeryTokenAsync(client);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.PurchaseDate", "2026-07-30"),
            new("Input.Lines[0].ProductId", ReviewSessionFixture.MilkProductId.ToString()),
            new("Input.Lines[0].Quantity", "2"),
            new("Input.Lines[0].UnitId", ReviewSessionFixture.LitreUnitId.ToString()),
            new("Input.Lines[0].Price", "3.99"),
            // LocationId omitted — the guard this test targets.
        };

        var resp = await client.PostAsync("/Intake/Manual", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty(_factory.Sessions.Sessions); // nothing committed

        var body = await resp.Content.ReadAsStringAsync();
        var doc = Parser.ParseDocument(body);
        var form = doc.QuerySelector("form[x-data^='manualPurchaseForm']")
            ?? throw new InvalidOperationException("Manual purchase form not found.");
        var xData = form.GetAttribute("x-data")
            ?? throw new InvalidOperationException("x-data attribute not found.");

        // The typed line's product survives the bounce into the reseeded rows[] — the acceptance
        // criterion: losing a half-typed shop to a validation bounce is the main failure mode.
        Assert.Contains($"\"productId\":\"{ReviewSessionFixture.MilkProductId}\"", xData);
        Assert.Contains("\"qty\":\"2\"", xData);
        Assert.Contains("\"price\":\"3.99\"", xData);
    }

    [Fact]
    public async Task New_product_line_missing_a_category_bounces_with_no_orphan_product()
    {
        var client = AuthClient();
        var token = await GetAntiforgeryTokenAsync(client);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.PurchaseDate", "2026-07-30"),
            new("Input.Lines[0].NewProductName", "Mystery item"),
            new("Input.Lines[0].Quantity", "1"),
            new("Input.Lines[0].UnitId", ReviewSessionFixture.EachUnitId.ToString()),
            new("Input.Lines[0].LocationId", ReviewSessionFixture.FridgeLocationId.ToString()),
            // NewProductCategoryId omitted.
        };

        var resp = await client.PostAsync("/Intake/Manual", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty(_factory.Sessions.Sessions);
        Assert.Empty(_factory.CreateProduct.Calls); // no orphan product minted before the failure

        var body = await resp.Content.ReadAsStringAsync();
        var doc = Parser.ParseDocument(body);
        var form = doc.QuerySelector("form[x-data^='manualPurchaseForm']")!;
        var xData = form.GetAttribute("x-data")!;
        Assert.Contains("\"newStapleName\":\"Mystery item\"", xData);
    }

    [Fact]
    public async Task Line_naming_both_an_existing_and_a_new_product_bounces_with_no_session_started()
    {
        // Server-side backstop for the retype-then-create sheet path (a client-side fix in Manual.cshtml
        // now clears draft.productId when the user switches to create — this test pins the rejection
        // regardless of that client fix, the same way RecipeEditorInlineCreateBouncePostTests pins its
        // JS-independent POST path).
        var client = AuthClient();
        var token = await GetAntiforgeryTokenAsync(client);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.PurchaseDate", "2026-07-30"),
            new("Input.Lines[0].ProductId", ReviewSessionFixture.MilkProductId.ToString()),
            new("Input.Lines[0].NewProductName", "Also new"),
            new("Input.Lines[0].NewProductCategoryId", ReviewSessionFixture.DairyCategoryId.ToString()),
            new("Input.Lines[0].Quantity", "1"),
            new("Input.Lines[0].UnitId", ReviewSessionFixture.LitreUnitId.ToString()),
            new("Input.Lines[0].LocationId", ReviewSessionFixture.FridgeLocationId.ToString()),
        };

        var resp = await client.PostAsync("/Intake/Manual", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty(_factory.Sessions.Sessions);
        Assert.Empty(_factory.CreateProduct.Calls);
    }
}
