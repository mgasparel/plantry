using System.Text.Json;
using Microsoft.Playwright;
using Npgsql;
using Plantry.Tests.E2E.Infrastructure;
using Plantry.Web.Intake;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E — the headline AI receipt-intake journey (plantry-zbk / Slice 6 done-when):
///   upload a receipt → review the parsed lines → resolve one against an existing product and
///   confirm the other as a brand-new product → commit → the stock shows up in the pantry, and
///   price observations are written.
///
/// Determinism: the web process runs with AI:UseFakeParser=true (set on the web resource by
/// <see cref="AppHostFixture"/>), so <see cref="FakeReceiptParser"/> stands in for the real Gemini
/// parser. No live AI call, no API key. The fake returns one high-confidence match against the
/// household's catalog (the product this test seeds first) and one unmatched line, both priced.
///
/// Price observations have no pantry UI surface, so they are asserted directly against the
/// pricing.price_observation table over the AppHost's owner connection (not subject to RLS).
///
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class ReceiptIntakeJourneyTests(AppHostFixture appHost) : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    private string BaseUrl => appHost.BaseUrl;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }

    [Fact(DisplayName = "Upload receipt → review → commit → stock in pantry + price observations written")]
    public async Task UploadReviewCommitLandsStockAndPrices()
    {
        var uniqueEmail = $"intake-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        // The product the fake parser will match (high confidence) against the catalog. Unique per run
        // so a fresh household's only product is unambiguously the one the fake picks (first hint).
        var matchedProductName = $"Smoke Beans {Guid.NewGuid():N}".Substring(0, 22);
        // The brand-new product the test confirms for the unmatched line.
        var newProductName = $"Mystery Bar {Guid.NewGuid():N}".Substring(0, 22);

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── Register a household (lands on Today home, logged in) ─────────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", "Intake Journey Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "Intake User");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── Seed one catalog product so the fake parser has a real match to suggest ──
            // Give it a DEFAULT LOCATION as well as a default unit: the fake parser's matched line carries no
            // unit/location, so the product's defaults make its server-side prefill COMPLETE — which lands it
            // in the deck flow's pre-checked "sure things" checklist (exercised via the bulk-confirm below).
            await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
            await page.WaitForURLAsync("**/Catalog/Products/Create");
            await page.FillAsync("[name='Input.Name']", matchedProductName);
            await page.SelectOptionAsync("[name='Input.DefaultUnitId']", new SelectOptionValue { Label = "ea — each" });
            await page.SelectOptionAsync("[name='Input.DefaultLocationId']", new SelectOptionValue { Label = "Pantry" });
            await page.ClickAsync("button:has-text('Create Product')");
            await page.WaitForURLAsync("**/Catalog/Products/**");

            // ── Upload a receipt (bytes are ignored by the fake parser) ──────────
            await page.GotoAsync($"{BaseUrl}/Intake/Upload");
            await page.WaitForURLAsync("**/Intake/Upload");

            // SetInputFilesAsync dispatches the change event, which triggers the Alpine
            // x-on:change handler ($el.form.requestSubmit()) — no separate submit button exists.
            await page.SetInputFilesAsync("input[type=file][name='Receipt']", new FilePayload
            {
                Name = "receipt.png",
                MimeType = "image/png",
                Buffer = TinyPngBytes(),
            });

            // The Parse handler runs the (fake) parse synchronously and HX-Redirects to the review form.
            await page.WaitForURLAsync("**/Intake/Review/**");

            // Two scanned lines drive the two deck-flow pools: the matched line (High confidence + a complete
            // prefill from the product's defaults) becomes a pre-checked "sure thing" in the checklist; the
            // unmatched no-match line becomes a create card in the judgement deck. Wait for both to render.
            await Assertions.Expect(page.Locator(".check-row")).ToHaveCountAsync(1);   // the sure thing
            await Assertions.Expect(page.Locator(".focus-card")).ToBeVisibleAsync();   // the deck card

            // ── Bulk-confirm the sure thing via the checklist "Confirm N matches" action ──
            // The matched line is pre-checked; one click promotes just the checked ids through ConfirmLines
            // (values re-derived server-side from the prefill) and it moves to the Confirmed list.
            await page.Locator(".step-foot button:has-text('Confirm 1 match')").ClickAsync();
            var matchedConfirmedRow = page.Locator(".import-row--confirmed", new() { HasText = matchedProductName });
            await Assertions.Expect(matchedConfirmedRow).ToBeVisibleAsync();
            await Assertions.Expect(matchedConfirmedRow.Locator(".import-row__confirmed-flag")).ToBeVisibleAsync();

            // ── Resolve the deck card (the unmatched line) as a brand-new product ──
            // A no-match line is a create card: fill the new-product name + category and the card's details
            // strip (qty/unit/location carry the same Edit.* field names as the confirmed-row edit drawer),
            // then confirm. Price is prefilled from the receipt and still produces a price observation.
            var deckCard = page.Locator(".focus-card");
            await deckCard.Locator("[name='Edit.NewProductName']").FillAsync(newProductName);
            await deckCard.Locator("[name='Edit.NewProductCategoryId']").SelectOptionAsync(new SelectOptionValue { Index = 1 });
            await deckCard.Locator("[name='Edit.Quantity']").FillAsync("1");
            await deckCard.Locator("[name='Edit.UnitId']").SelectOptionAsync(new SelectOptionValue { Label = "ea — each" });
            await deckCard.Locator("[name='Edit.LocationId']").SelectOptionAsync(new SelectOptionValue { Label = "Pantry" });
            await deckCard.Locator("button:has-text('Add new & next')").ClickAsync();

            // Both lines confirmed → the new product shows in the Confirmed list and the deck empties.
            var newConfirmedRow = page.Locator(".import-row--confirmed", new() { HasText = newProductName });
            await Assertions.Expect(newConfirmedRow).ToBeVisibleAsync();

            // ── Commit — both lines confirmed, so the Commit button is enabled ──
            // The island recomputes the commit-bar state client-side after each confirmation, so the button
            // should be enabled without a reload once nothing is left in the sure/needs pools.
            var commitButton = page.Locator(".commit-bar button:has-text('Add to pantry')");
            await Assertions.Expect(commitButton).ToBeEnabledAsync();
            await commitButton.ClickAsync();

            // On commit the page HX-Redirects to the Done screen, then the user navigates to the pantry.
            await page.WaitForURLAsync("**/Intake/Done/**");
            await page.ClickAsync("a:has-text('View pantry')");
            await page.WaitForURLAsync("**/Pantry**");

            // ── Assert: both products now hold stock in the pantry ──────────────
            var matchedPantryRow = page.Locator("tr", new() { HasText = matchedProductName });
            await Assertions.Expect(matchedPantryRow).ToBeVisibleAsync();
            await Assertions.Expect(matchedPantryRow).ToContainTextAsync("2 ea");

            var newPantryRow = page.Locator("tr", new() { HasText = newProductName });
            await Assertions.Expect(newPantryRow).ToBeVisibleAsync();
            await Assertions.Expect(newPantryRow).ToContainTextAsync("1 ea");

            // ── Assert: price observations were written for this commit ─────────
            // Asserted directly against the read model (no pantry UI surface for prices). Scope the
            // checks to this test's uniquely named products because the fake parser uses one merchant
            // across all receipt-intake journeys in the shared AppHost database.
            var (matchedProductId, matchedJournalCount) =
                await FindCatalogProductAndIntakeJournalCountAsync(matchedProductName);
            var (newProductId, newJournalCount) =
                await FindCatalogProductAndIntakeJournalCountAsync(newProductName);
            Assert.Equal(1, matchedJournalCount);
            Assert.Equal(1, newJournalCount);
            Assert.Equal(1, await CountPriceObservationsForProductAsync(FakeReceiptParser.FixedMerchant, matchedProductId));
            Assert.Equal(1, await CountPriceObservationsForProductAsync(FakeReceiptParser.FixedMerchant, newProductId));
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-intake.zip" });
        }
    }

    [Fact(DisplayName = "Two unmatched lines can select one staged alias and commit two stocks")]
    public async Task TwoUnmatchedLinesReuseOneStagedProduct()
    {
        var uniqueEmail = $"staged-intake-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";
        var stagedProductName = $"Shared Oat Milk {Guid.NewGuid():N}".Substring(0, 28);

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

        // Register a fresh household but deliberately do not seed a catalog product. The fake parser
        // emits two unmatched lines in this mode, giving the review a real staged-alias choice to carry
        // from the first card to the second.
        await page.GotoAsync($"{BaseUrl}/Account/Register");
        await page.FillAsync("[name='Input.HouseholdName']", "Staged Intake Household");
        await page.FillAsync("[name='Input.Email']", uniqueEmail);
        await page.FillAsync("[name='Input.DisplayName']", "Staged Intake User");
        await page.FillAsync("[name='Input.Password']", password);
        await page.ClickAsync("button[type=submit]");
        await page.WaitForURLAsync("**/Today**");

        await page.GotoAsync($"{BaseUrl}/Intake/Upload");
        await page.SetInputFilesAsync("input[type=file][name='Receipt']", new FilePayload
        {
            Name = "two-unmatched.png",
            MimeType = "image/png",
            Buffer = TinyPngBytes(),
        });
        await page.WaitForURLAsync("**/Intake/Review/**");

        // First unmatched card: create and stage the alias, then advance to the second card.
        var firstCard = page.Locator(".focus-card");
        await Assertions.Expect(firstCard).ToBeVisibleAsync();
        await firstCard.Locator("[name='Edit.NewProductName']").FillAsync(stagedProductName);
        await firstCard.Locator("[name='Edit.NewProductCategoryId']").SelectOptionAsync(new SelectOptionValue { Index = 1 });
        await firstCard.Locator("[name='Edit.Quantity']").FillAsync("1");
        await firstCard.Locator("[name='Edit.UnitId']").SelectOptionAsync(new SelectOptionValue { Label = "ea — each" });
        await firstCard.Locator("[name='Edit.LocationId']").SelectOptionAsync(new SelectOptionValue { Label = "Pantry" });
        var saveResponseTask = page.WaitForResponseAsync(response =>
            response.Url.Contains("handler=SaveLine", StringComparison.OrdinalIgnoreCase));
        await firstCard.Locator("button:has-text('Add new & next')").ClickAsync();
        await saveResponseTask;

        // Second unmatched card: open Change match, search the server-returned staged option, and select
        // it explicitly. The option keeps ProductId blank while carrying stagedProductId in SaveLine.
        await Assertions.Expect(page.Locator(".focus-card"))
            .ToContainTextAsync(FakeReceiptParser.SecondUnmatchedReceiptText);
        var secondCard = page.Locator(".focus-card");
        await Assertions.Expect(secondCard).ToBeVisibleAsync();
        await secondCard.Locator("button:has-text('Change match')").ClickAsync();
        var search = secondCard.Locator("input[role='combobox']");
        await search.FillAsync(stagedProductName);
        var stagedOption = secondCard.Locator(".searchable-select__option--staged", new() { HasText = stagedProductName });
        await Assertions.Expect(stagedOption).ToBeVisibleAsync();
        await stagedOption.ClickAsync();
        await secondCard.Locator("[name='Edit.LocationId']").SelectOptionAsync(new SelectOptionValue { Label = "Pantry" });
        await secondCard.Locator("button:has-text('Add new & next')").ClickAsync();

        var commitButton = page.Locator(".commit-bar button:has-text('Add to pantry')");
        await Assertions.Expect(commitButton).ToBeEnabledAsync();
        await commitButton.ClickAsync();
        await page.WaitForURLAsync("**/Intake/Done/**");

        var (productId, journalCount) = await FindCatalogProductAndIntakeJournalCountAsync(stagedProductName);
        Assert.NotEqual(Guid.Empty, productId);
        Assert.Equal(2, journalCount);
        Assert.Equal(2, await CountPriceObservationsForProductAsync(FakeReceiptParser.FixedMerchant, productId));
    }

    [Fact(DisplayName = "Same line can rematch an inline-staged product without navigation")]
    public async Task SameLineCanReopenAndRematchItsStagedProduct()
    {
        var uniqueEmail = $"same-line-staged-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";
        var stagedProductName = $"Same Line Oat Milk {Guid.NewGuid():N}".Substring(0, 28);

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

        await page.GotoAsync($"{BaseUrl}/Account/Register");
        await page.FillAsync("[name='Input.HouseholdName']", "Same Line Staged Household");
        await page.FillAsync("[name='Input.Email']", uniqueEmail);
        await page.FillAsync("[name='Input.DisplayName']", "Same Line Staged User");
        await page.FillAsync("[name='Input.Password']", password);
        await page.ClickAsync("button[type=submit]");
        await page.WaitForURLAsync("**/Today**");

        await page.GotoAsync($"{BaseUrl}/Intake/Upload");
        await page.SetInputFilesAsync("input[type=file][name='Receipt']", new FilePayload
        {
            Name = "same-line-staged.png",
            MimeType = "image/png",
            Buffer = TinyPngBytes(),
        });
        await page.WaitForURLAsync("**/Intake/Review/**");
        await page.EvaluateAsync("() => { window.__plantrySameLineReviewMarker = 'plantry-o0og-review'; }");

        // Confirm the first unmatched line as a deferred staged product.
        var firstCard = page.Locator(".focus-card");
        await Assertions.Expect(firstCard).ToBeVisibleAsync();
        await firstCard.Locator("[name='Edit.NewProductName']").FillAsync(stagedProductName);
        await firstCard.Locator("[name='Edit.NewProductCategoryId']").SelectOptionAsync(new SelectOptionValue { Index = 1 });
        await firstCard.Locator("[name='Edit.Quantity']").FillAsync("1");
        await firstCard.Locator("[name='Edit.UnitId']").SelectOptionAsync(new SelectOptionValue { Label = "ea — each" });
        await firstCard.Locator("[name='Edit.LocationId']").SelectOptionAsync(new SelectOptionValue { Label = "Pantry" });
        var firstSaveResponseTask = page.WaitForResponseAsync(response =>
            response.Url.Contains("handler=SaveLine", StringComparison.OrdinalIgnoreCase));
        await firstCard.Locator("button:has-text('Add new & next')").ClickAsync();
        var firstSaveResponse = await firstSaveResponseTask;
        using var firstSaveBody = JsonDocument.Parse(await firstSaveResponse.TextAsync());
        var stagedProductId = firstSaveBody.RootElement.GetProperty("stagedProductId").GetGuid();
        Assert.NotEqual(Guid.Empty, stagedProductId);

        // SaveLine stages the alias only. No Catalog product exists until the eventual commit.
        Assert.Equal(0, await CountCatalogProductsAsync(stagedProductName));

        // Reopen the very same confirmed row, then put it back into Change match without a reload.
        var confirmedRow = page.Locator(".import-row--confirmed", new() { HasText = stagedProductName });
        await Assertions.Expect(confirmedRow).ToBeVisibleAsync();
        Assert.Equal(
            "plantry-o0og-review",
            await page.EvaluateAsync<string>("() => window.__plantrySameLineReviewMarker"));
        await confirmedRow.Locator(".import-row__main").ClickAsync();
        var reopenResponseTask = page.WaitForResponseAsync(response =>
            response.Url.Contains("handler=ReopenLine", StringComparison.OrdinalIgnoreCase));
        await confirmedRow.Locator("button:has-text('Wrong product — review again')").ClickAsync();
        await reopenResponseTask;

        var reopenedCard = page.Locator(".focus-card");
        await Assertions.Expect(reopenedCard).ToContainTextAsync(FakeReceiptParser.UnmatchedReceiptText);
        Assert.Equal(
            "plantry-o0og-review",
            await page.EvaluateAsync<string>("() => window.__plantrySameLineReviewMarker"));
        await reopenedCard.Locator("button:has-text('Change match')").ClickAsync();
        var search = reopenedCard.Locator("input[role='combobox']");
        await search.FillAsync(stagedProductName);
        var stagedOption = reopenedCard.Locator(".searchable-select__option--staged", new() { HasText = stagedProductName });
        await Assertions.Expect(stagedOption).ToBeVisibleAsync();
        await Assertions.Expect(stagedOption).ToHaveCountAsync(1);
        await stagedOption.ClickAsync();

        // Selecting the alias keeps ProductId empty and carries the staged identity through SaveLine.
        var rematchSaveRequestTask = page.WaitForRequestAsync(request =>
            request.Url.Contains("handler=SaveLine", StringComparison.OrdinalIgnoreCase));
        await reopenedCard.Locator("button:has-text('Add new & next')").ClickAsync();
        var rematchSaveRequest = await rematchSaveRequestTask;
        using var rematchSaveBody = JsonDocument.Parse(rematchSaveRequest.PostData!);
        var rematchPayload = rematchSaveBody.RootElement;
        Assert.Equal(JsonValueKind.Null, rematchPayload.GetProperty("productId").ValueKind);
        Assert.Equal(stagedProductId, rematchPayload.GetProperty("stagedProductId").GetGuid());

        // Finish the review by rejecting the second fake unmatched line; the rematched line is committed normally.
        var secondCard = page.Locator(".focus-card");
        await Assertions.Expect(secondCard).ToContainTextAsync(FakeReceiptParser.SecondUnmatchedReceiptText);
        await secondCard.Locator("button:has-text('Not pantry stock')").ClickAsync();

        var commitButton = page.Locator(".commit-bar button:has-text('Add to pantry')");
        await Assertions.Expect(commitButton).ToBeEnabledAsync();
        await commitButton.ClickAsync();
        await page.WaitForURLAsync("**/Intake/Done/**");

        var (productId, journalCount) = await FindCatalogProductAndIntakeJournalCountAsync(stagedProductName);
        Assert.NotEqual(Guid.Empty, productId);
        Assert.Equal(1, journalCount);
        Assert.Equal(1, await CountPriceObservationsForProductAsync(FakeReceiptParser.FixedMerchant, productId));
    }

    private async Task<(Guid ProductId, int JournalCount)> FindCatalogProductAndIntakeJournalCountAsync(string productName)
    {
        await using var conn = new NpgsqlConnection(appHost.DbConnectionString);
        await conn.OpenAsync();

        await using var count = new NpgsqlCommand(
            "SELECT COUNT(*) FROM catalog.products WHERE name = @name", conn);
        count.Parameters.AddWithValue("@name", productName);
        Assert.Equal(1, Convert.ToInt32(await count.ExecuteScalarAsync()));

        await using var product = new NpgsqlCommand(
            "SELECT id FROM catalog.products WHERE name = @name", conn);
        product.Parameters.AddWithValue("@name", productName);
        var productValue = await product.ExecuteScalarAsync();
        var productId = productValue is Guid id ? id : Guid.Empty;

        await using var journals = new NpgsqlCommand(
            "SELECT COUNT(*) FROM inventory.stock_journal_entry WHERE product_id = @productId AND source_type = 'Intake'",
            conn);
        journals.Parameters.AddWithValue("@productId", productId);
        return (productId, Convert.ToInt32(await journals.ExecuteScalarAsync()));
    }

    private async Task<int> CountCatalogProductsAsync(string productName)
    {
        await using var conn = new NpgsqlConnection(appHost.DbConnectionString);
        await conn.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM catalog.products WHERE name = @name", conn);
        command.Parameters.AddWithValue("@name", productName);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<int> CountPriceObservationsForProductAsync(string merchantText, Guid productId)
    {
        await using var conn = new NpgsqlConnection(appHost.DbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM pricing.price_observation WHERE merchant_text = @merchant AND source = 'Purchase' AND product_id = @productId",
            conn);
        cmd.Parameters.AddWithValue("@merchant", merchantText);
        cmd.Parameters.AddWithValue("@productId", productId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    /// <summary>Smallest valid 1×1 PNG. The fake parser ignores the bytes, but the upload page enforces an
    /// image content type and a non-empty body, so a real (tiny) PNG keeps the upload path honest.</summary>
    private static byte[] TinyPngBytes() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
}
