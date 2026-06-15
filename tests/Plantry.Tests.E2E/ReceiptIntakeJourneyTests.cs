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
            await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
            await page.WaitForURLAsync("**/Catalog/Products/Create");
            await page.FillAsync("[name='Input.Name']", matchedProductName);
            await page.SelectOptionAsync("[name='Input.DefaultUnitId']", new SelectOptionValue { Label = "ea — each" });
            await page.ClickAsync("button:has-text('Add product')");
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

            // Two rows: the matched line (high confidence) and the unmatched line.
            var rows = page.Locator(".import-row");
            await Assertions.Expect(rows).ToHaveCountAsync(2);

            // ── Resolve the matched line against the existing seeded product ─────
            // The matched (high-confidence) row renders with its edit drawer closed. Capture its stable
            // DOM id first: confirming swaps the row's outerHTML (its state class flips matched→confirmed),
            // so a class-based locator would go stale — re-locate by #id after each action instead.
            var matchedRowId = await page.Locator(".import-row.import-row--matched").GetAttributeAsync("id")
                ?? throw new InvalidOperationException("Matched import row had no id.");
            var matchedRow = page.Locator($"#{matchedRowId}");
            await matchedRow.Locator(".import-row__toggle").ClickAsync();

            // Pick the seeded product via the row's searchable-select, then fill qty/unit/location.
            // Price is prefilled from the receipt and carried in a hidden input — there is no visible
            // price field to fill (and the prefilled value still produces a price observation on commit).
            var productSearch = matchedRow.Locator("input[role='combobox']");
            await productSearch.FillAsync(matchedProductName);
            var productOption = matchedRow.Locator(".searchable-select__listbox li[role='option']",
                new() { HasText = matchedProductName });
            await Assertions.Expect(productOption).ToBeVisibleAsync();
            await productOption.ClickAsync();

            await matchedRow.Locator("[name='Edit.Quantity']").FillAsync("2");
            await matchedRow.Locator("[name='Edit.UnitId']").SelectOptionAsync(new SelectOptionValue { Label = "ea — each" });
            await matchedRow.Locator("[name='Edit.LocationId']").SelectOptionAsync(new SelectOptionValue { Label = "Pantry" });
            await matchedRow.Locator("button:has-text('Confirm item')").ClickAsync();

            // Row swaps (same #id) to the confirmed state.
            await Assertions.Expect(matchedRow.Locator(".import-row__confirmed-flag")).ToBeVisibleAsync();

            // ── Confirm the unmatched line as a brand-new product ───────────────
            // The unmatched row renders with its drawer already open. Capture its id for the same reason.
            var unmatchedRowId = await page.Locator(".import-row.import-row--unmatched").GetAttributeAsync("id")
                ?? throw new InvalidOperationException("Unmatched import row had no id.");
            var unmatchedRow = page.Locator($"#{unmatchedRowId}");

            // Switch the row to "New product" mode (the drawer's toggle button) and fill the create fields.
            // As with the matched row, price is prefilled from the receipt into a hidden input.
            await unmatchedRow.Locator("button:has-text('Add as new product')").ClickAsync();
            await unmatchedRow.Locator("[name='Edit.NewProductName']").FillAsync(newProductName);
            await unmatchedRow.Locator("[name='Edit.NewProductCategoryId']").SelectOptionAsync(new SelectOptionValue { Index = 1 });
            await unmatchedRow.Locator("[name='Edit.Quantity']").FillAsync("1");
            await unmatchedRow.Locator("[name='Edit.UnitId']").SelectOptionAsync(new SelectOptionValue { Label = "ea — each" });
            await unmatchedRow.Locator("[name='Edit.LocationId']").SelectOptionAsync(new SelectOptionValue { Label = "Pantry" });
            await unmatchedRow.Locator("button:has-text('Confirm item')").ClickAsync();

            await Assertions.Expect(unmatchedRow.Locator(".import-row__confirmed-flag")).ToBeVisibleAsync();

            // ── Commit — both lines confirmed, so the Commit button is enabled ──
            // The OOB commit-bar swap (hx-swap-oob) updates the bar after each row confirmation, so
            // the button should be enabled without a reload once both lines are confirmed.
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
            // Asserted directly against the read model (no pantry UI surface for prices). The fake parser
            // reports a fixed merchant, so the two priced lines must produce two observations for it.
            var observations = await CountPriceObservationsAsync(FakeReceiptParser.FixedMerchant);
            Assert.Equal(2, observations);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-intake.zip" });
        }
    }

    /// <summary>Counts price observations recorded for a merchant. Reads as the database owner, which is
    /// not subject to RLS, so it sees the rows written by this test's household.</summary>
    private async Task<int> CountPriceObservationsAsync(string merchantText)
    {
        await using var conn = new NpgsqlConnection(appHost.DbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM pricing.price_observation WHERE merchant_text = @m AND source = 'Purchase'",
            conn);
        cmd.Parameters.AddWithValue("@m", merchantText);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    /// <summary>Smallest valid 1×1 PNG. The fake parser ignores the bytes, but the upload page enforces an
    /// image content type and a non-empty body, so a real (tiny) PNG keeps the upload path honest.</summary>
    private static byte[] TinyPngBytes() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
}
