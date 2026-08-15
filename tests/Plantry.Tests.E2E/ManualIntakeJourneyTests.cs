using Microsoft.Playwright;
using Npgsql;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E — the manual purchase entry journey (plantry-45ba.3): /Intake/Manual's entire line-entry
/// mechanism is client-side Alpine (the repeating rows[] array, the shared
/// _ProductSearchCreateSheet add/edit sheet, the unitId/newStapleUnit reconciliation watchers,
/// saveSheet()'s dual-render into hidden Input.Lines[n] inputs) which the L4 page tests bypass by
/// POSTing the hidden inputs directly — so this journey is the only automated tier that executes it.
///
/// The journey deliberately walks the create view's distinct ENTRY PATHS (enumerated in
/// Manual.cshtml's init() comment):
///   - Line 1: search-pick path — picking a seeded product must prefill unit, location, and the
///     server-computed expiry (ReviewPrefill) into the sheet's fields.
///   - Line 2: pick-then-retype-then-create path — REGRESSION test for the pass-3 critic finding:
///     selectProduct() sets draft.unitId while still in the search view (where the draft watchers
///     are gated off); flipping to the create view must re-run the reconciliation via the
///     sheetView watcher, so the Create button is ENABLED without the user ever touching the
///     Defaults "Stock unit" select.
/// Both lines then commit in ONE submit and land on the committed session detail.
///
/// Modelled on ReceiptIntakeJourneyTests / RecipeAuthorJourneyTests (fresh household per run,
/// product seeded via /Catalog/Products/Create).
///
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class ManualIntakeJourneyTests(AppHostFixture appHost) : IAsyncLifetime
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

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>Registers a fresh household and returns the page (landed on /Today).</summary>
    private async Task<IPage> RegisterHouseholdAsync(IBrowserContext context, string email, string householdName)
    {
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

        await page.GotoAsync($"{BaseUrl}/Account/Register");
        await page.WaitForURLAsync("**/Account/Register");
        await page.FillAsync("[name='Input.HouseholdName']", householdName);
        await page.FillAsync("[name='Input.Email']", email);
        await page.FillAsync("[name='Input.DisplayName']", "Test User");
        await page.FillAsync("[name='Input.Password']", "testpass1");
        await page.ClickAsync("button[type=submit]");
        await page.WaitForURLAsync("**/Today**");

        return page;
    }

    /// <summary>
    /// Seeds one catalog product with a default unit ("ea"), default location ("Fridge" — seeded
    /// for every fresh household), and DefaultDueDays = 7 (set on the product detail page — the
    /// create form has no due-days field), then returns the (unitId, locationId) option values so
    /// the journey can assert the sheet prefills exactly them.
    /// </summary>
    private async Task<(string UnitId, string LocationId)> SeedProductAsync(IPage page, string productName)
    {
        await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
        await page.WaitForURLAsync("**/Catalog/Products/Create");
        await page.FillAsync("[name='Input.Name']", productName);
        await page.SelectOptionAsync("[name='Input.DefaultUnitId']", new SelectOptionValue { Label = "ea — each" });
        await page.SelectOptionAsync("[name='Input.DefaultLocationId']", new SelectOptionValue { Label = "Fridge" });
        var unitId = await page.InputValueAsync("[name='Input.DefaultUnitId']");
        var locationId = await page.InputValueAsync("[name='Input.DefaultLocationId']");
        await page.ClickAsync("button:has-text('Create Product')");
        await page.WaitForURLAsync("**/Catalog/Products/**");

        // The create form carries no DefaultDueDays field; set it on the product detail page so the
        // search-pick path has a server-computed expiry (ReviewPrefill) to prefill.
        await page.FillAsync("[name='Input.DefaultDueDays']", "7");
        await page.ClickAsync("button:has-text('Save changes')");
        await page.WaitForURLAsync("**/Catalog/Products/**");

        return (unitId, locationId);
    }

    [Fact(DisplayName = "plantry-45ba.3: manual intake — search-pick prefills unit/location/expiry; pick-then-retype-then-create leaves Create ENABLED; both lines commit in one submit")]
    public async Task ManualIntake_PickAndInlineCreate_CommitInOneSubmit()
    {
        var email = $"manual-intake-{Guid.NewGuid():N}@test.local";

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await RegisterHouseholdAsync(context, email, "Manual Intake Household");

            // ── Seed a product with default unit + location + due-days ────────────
            var milkName = $"Whole Milk {Guid.NewGuid():N}".Substring(0, 22);
            var (unitId, locationId) = await SeedProductAsync(page, milkName);

            // ── Open /Intake/Manual ───────────────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Intake/Manual");
            await page.WaitForURLAsync("**/Intake/Manual");

            var sheet = page.Locator("#manual-purchase-form .sheet");
            // Search-view Quantity/Unit are the label-adjacent controls (the create-view fields have
            // ids but are x-show hidden here; #manual-line-price is also type=number so ':visible'
            // alone would be ambiguous).
            // ':visible' disambiguates from the create-view Quantity field (same label, x-show hidden
            // while in the search view).
            var searchQty = sheet.Locator(".form-grid__field:has(> label:text-is('Quantity')) input[type='number']:visible");
            var searchUnit = sheet.Locator(".form-grid__field:has(> label:text-is('Unit')) select");
            var lineLocation = sheet.Locator("#manual-line-location");
            var lineExpiry = sheet.Locator("#manual-line-expiry");
            var linePrice = sheet.Locator("#manual-line-price");
            var addButton = sheet.Locator(".sheet__actions button.btn--primary").First;   // search view
            var createButton = sheet.Locator(".sheet__actions button.btn--primary").Last; // create view

            // ── Line 1: pick the seeded product — unit/location/expiry auto-prefill ──
            await page.ClickAsync("button:has-text('Add line')");
            await Assertions.Expect(sheet).ToBeVisibleAsync();

            // Type char-by-char so the htmx keyup search trigger fires (FillAsync sets .value silently).
            await sheet.Locator("input[role='combobox']").PressSequentiallyAsync(milkName.Substring(0, 8));
            var option = sheet.Locator(".searchable-select__listbox li[role='option']", new() { HasText = milkName });
            await Assertions.Expect(option).ToBeVisibleAsync();
            await option.ClickAsync();

            // Server-side ReviewPrefill defaults land in the sheet fields (auto-retrying Expect).
            await Assertions.Expect(searchUnit).ToHaveValueAsync(unitId);
            await Assertions.Expect(lineLocation).ToHaveValueAsync(locationId);
            // Expiry = purchase date + DefaultDueDays (7), computed server-side — assert it arrived.
            await Assertions.Expect(lineExpiry).Not.ToHaveValueAsync("");

            await searchQty.FillAsync("2");
            await linePrice.FillAsync("3.99");
            await addButton.ClickAsync();
            await Assertions.Expect(sheet).Not.ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".manual-line-row__summary", new() { HasText = milkName }))
                .ToBeVisibleAsync();

            // ── Enter in the store combobox must NOT implicit-submit the half-entered shop ──
            // The natural "confirm my pick" gesture with a row already added would otherwise fire
            // HTML implicit submission and commit the purchase for real (no resume/undo surface).
            await page.Locator("#manual-store").PressSequentiallyAsync("Corner");
            await page.Locator("#manual-store").PressAsync("Enter");
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/Intake/Manual$"));

            // ── Line 2: pick-then-retype-then-create (REGRESSION for the sheetView watcher) ──
            var newProductName = $"Oat Cream {Guid.NewGuid():N}".Substring(0, 20);

            await page.ClickAsync("button:has-text('Add line')");
            await Assertions.Expect(sheet).ToBeVisibleAsync();

            var combobox = sheet.Locator("input[role='combobox']");
            await combobox.FillAsync("");
            await combobox.PressSequentiallyAsync(milkName.Substring(0, 8));
            await Assertions.Expect(option).ToBeVisibleAsync();
            await option.ClickAsync(); // sets draft.unitId/locationId/expiry while sheetView === 'search'
            await Assertions.Expect(searchUnit).ToHaveValueAsync(unitId);

            // Retype a different name and enter the create view via "+ Create … as a new product".
            await combobox.FillAsync("");
            await combobox.PressSequentiallyAsync(newProductName.Substring(0, 8));
            await sheet.Locator("button:has-text('as a new product')").ClickAsync();
            var nameInput = sheet.Locator("input[placeholder='Product name (e.g. Whole milk)']");
            await Assertions.Expect(nameInput).ToBeVisibleAsync();

            // THE regression assertions: flipping sheetView must mirror the picked unit into
            // draft.newStapleUnit (the sheetView watcher), so — without touching the Defaults
            // "Stock unit" select — the Create button is ENABLED, not permanently disabled.
            await Assertions.Expect(sheet.Locator("#create-product-unit")).ToHaveValueAsync(unitId);
            await Assertions.Expect(createButton).ToBeEnabledAsync();

            await nameInput.FillAsync(newProductName);
            await sheet.Locator("#create-product-qty").FillAsync("1");
            await sheet.Locator("#create-product-category").SelectOptionAsync(new SelectOptionValue { Label = "Dairy & Eggs" });
            // Keep the product default independent from this purchase's lot destination.
            await sheet.Locator("#create-product-location").SelectOptionAsync(new SelectOptionValue { Label = "Fridge" });
            await lineLocation.SelectOptionAsync(new SelectOptionValue { Label = "Pantry" });

            await createButton.ClickAsync();
            await Assertions.Expect(sheet).Not.ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".manual-line-row__summary", new() { HasText = newProductName }))
                .ToBeVisibleAsync();

            // ── One submit commits both lines and lands on the session detail ─────
            await page.ClickAsync("button[type=submit]:has-text('Log purchase')");
            await page.WaitForURLAsync("**/Intake/Session/**");

            await Assertions.Expect(page.GetByText(milkName).First).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText(newProductName).First).ToBeVisibleAsync();

            await using var conn = new NpgsqlConnection(appHost.DbConnectionString);
            await conn.OpenAsync();
            await using var productCommand = new NpgsqlCommand(
                "SELECT p.id, default_location.name FROM catalog.products p LEFT JOIN catalog.locations default_location ON default_location.id = p.default_location_id AND default_location.household_id = p.household_id WHERE p.name = @name", conn);
            productCommand.Parameters.AddWithValue("@name", newProductName);
            await using var productReader = await productCommand.ExecuteReaderAsync();
            Assert.True(await productReader.ReadAsync());
            var newProductId = productReader.GetGuid(0);
            var defaultLocationName = productReader.IsDBNull(1) ? null : productReader.GetString(1);
            await productReader.DisposeAsync();

            await using var lotCommand = new NpgsqlCommand(
                "SELECT l.name FROM inventory.stock_entry e JOIN catalog.locations l ON l.id = e.location_id AND l.household_id = e.household_id WHERE e.product_id = @productId AND e.quantity > 0 ORDER BY e.created_at DESC LIMIT 1", conn);
            lotCommand.Parameters.AddWithValue("@productId", newProductId);
            var lotLocationName = (string)(await lotCommand.ExecuteScalarAsync())!;

            Assert.Equal("Fridge", defaultLocationName);
            Assert.Equal("Pantry", lotLocationName);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-manual-intake.zip" });
        }
    }
}
