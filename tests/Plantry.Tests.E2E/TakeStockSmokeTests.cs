using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E smoke test for the Take Stock walk (P4-4b, J1/J2/J4).
///
/// Journey under test (mirrors J2/J4 from inventory-takestock-journeys.md):
///   Register household → create stock-holding product → add stock to Pantry location →
///   navigate to Take Stock index → open Pantry location walk page →
///   change count via stepper → Save → Pantry shows updated quantity.
///
/// This test proves the end-to-end route (UI → Alpine → POST Save → SaveCountsCommand →
/// ProductStock.Consume/AddStock) works against the live Aspire stack.
///
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class TakeStockSmokeTests(AppHostFixture appHost) : IAsyncLifetime
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

    [Fact(DisplayName = "Take Stock: lot escape hatch — expand lots, adjust two lots, save reflects each (J3/P4-5)")]
    public async Task TakeStock_LotEscapeHatch_ExpandAndAdjustTwoLots()
    {
        var uniqueEmail = $"ts-lot-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";
        var productName = $"TS Lot {Guid.NewGuid():N}".Substring(0, 16);

        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── Register a fresh household ─────────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", "TS Lot E2E Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "TS Lot E2E User");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── Create a stock-holding product ────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
            await page.WaitForURLAsync("**/Catalog/Products/Create");
            await page.FillAsync("[name='Input.Name']", productName);
            await page.SelectOptionAsync("[name='Input.DefaultUnitId']", new SelectOptionValue { Label = "g — gram" });
            await page.ClickAsync("button:has-text('Add product')");
            await page.WaitForURLAsync("**/Catalog/Products/**");

            // ── Add two lots of stock in Pantry (200g + 300g = 500g) ──────────────
            // Lot 1: 200g, no expiry
            await page.GotoAsync($"{BaseUrl}/Pantry");
            await page.WaitForURLAsync("**/Pantry**");
            await page.ClickAsync("button:has-text('Add stock')");
            await Assertions.Expect(page.Locator("#sheet-host .sheet__panel")).ToBeVisibleAsync();
            var productSearch = page.Locator("#sheet-host .sheet__panel input[role='combobox']");
            await productSearch.FillAsync(productName);
            var productOption = page.Locator(".searchable-select__listbox li[role='option']", new() { HasText = productName });
            await Assertions.Expect(productOption).ToBeVisibleAsync();
            await productOption.ClickAsync();
            await page.FillAsync("[name='Input.Quantity']", "200");
            await page.SelectOptionAsync("[name='Input.UnitId']", new SelectOptionValue { Label = "g — gram" });
            await page.SelectOptionAsync("[name='Input.LocationId']", new SelectOptionValue { Label = "Pantry" });
            await page.ClickAsync("button:has-text('Add to pantry')");
            var pantryRow = page.Locator("tr", new() { HasText = productName });
            await Assertions.Expect(pantryRow).ToBeVisibleAsync();

            // Lot 2: 300g, no expiry
            await page.ClickAsync("button:has-text('Add stock')");
            await Assertions.Expect(page.Locator("#sheet-host .sheet__panel")).ToBeVisibleAsync();
            await productSearch.FillAsync(productName);
            await Assertions.Expect(productOption).ToBeVisibleAsync();
            await productOption.ClickAsync();
            await page.FillAsync("[name='Input.Quantity']", "300");
            await page.SelectOptionAsync("[name='Input.UnitId']", new SelectOptionValue { Label = "g — gram" });
            await page.SelectOptionAsync("[name='Input.LocationId']", new SelectOptionValue { Label = "Pantry" });
            await page.ClickAsync("button:has-text('Add to pantry')");
            await Assertions.Expect(pantryRow).ToContainTextAsync("500 g", new LocatorAssertionsToContainTextOptions { Timeout = 30000 });

            // ── Navigate to Take Stock → open Pantry walk ─────────────────────────
            await page.GotoAsync($"{BaseUrl}/pantry/take-stock");
            await page.WaitForURLAsync("**/pantry/take-stock**");
            var pantryLink = page.Locator(".catalog-list a.catalog-list__primary", new() { HasText = "Pantry" });
            await Assertions.Expect(pantryLink).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            await pantryLink.ClickAsync();
            await page.WaitForURLAsync("**/pantry/take-stock/**");

            // ── Expand the lot panel ──────────────────────────────────────────────
            var adjustLotsBtn = page.Locator(".take-stock-row__lots-btn", new() { HasText = "Adjust lots" });
            await Assertions.Expect(adjustLotsBtn).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            await adjustLotsBtn.ClickAsync();

            // The lot panel should appear with both lots.
            var lotPanel = page.Locator(".take-stock-lot-panel");
            await Assertions.Expect(lotPanel).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            // Two lot rows should appear.
            await Assertions.Expect(page.Locator(".take-stock-lot-panel__lot")).ToHaveCountAsync(2, new LocatorAssertionsToHaveCountOptions { Timeout = 30000 });

            // ── Adjust Alpine state and trigger save via $data.save() directly ──────────
            // Alpine.$data(panel) returns a reactive proxy of the merged data stack.
            // setLotAmount and save() are accessible directly. This is more reliable than
            // synthetic button click events for testing async Alpine methods.
            var saveUrl = await page.EvaluateAsync<string>(@"
                () => {
                    const panel = document.querySelector('.take-stock-lot-panel');
                    if (!panel || !window.Alpine) return null;
                    const data = window.Alpine.$data(panel);
                    if (!data || typeof data.setLotAmount !== 'function') return null;
                    const lotIds = Object.keys(data.lots ?? {});
                    if (lotIds.length >= 1) data.setLotAmount(lotIds[0], 50);
                    if (lotIds.length >= 2) {
                        data.setLotAmount(lotIds[1], 80);
                        data.setSpoiled(lotIds[1], true);
                    }
                    // Extract the save URL from the button's x-on:click attribute.
                    const btn = panel.querySelector('button[x-on\\:click]');
                    const clickAttr = btn ? btn.getAttribute('x-on:click') : '';
                    const m = clickAttr && clickAttr.match(/save\('([^']+)'\)/);
                    return m ? m[1] : null;
                }
            ");

            // Wait for button to enable (isDirty() reactive update).
            var saveBtn = page.Locator(".take-stock-lot-panel button:has-text('Save lot changes')");
            await Assertions.Expect(saveBtn).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 15000 });

            // ── Save lot changes ──────────────────────────────────────────────────
            await saveBtn.ClickAsync();

            // After a successful save, the panel is collapsed by onLotsSaved (parent removes innerHTML).
            // Wait for the lot panel to disappear as a proxy for save success.
            await Assertions.Expect(lotPanel).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 30000 });

            // ── Verify Pantry reflects each lot change ────────────────────────────
            // Stock was 500g; lot 1 reduced by 50 + lot 2 reduced by 80 = 130g removed → 370g
            await page.GotoAsync($"{BaseUrl}/Pantry");
            await page.WaitForURLAsync("**/Pantry**");
            var updatedRow = page.Locator("tr", new() { HasText = productName });
            await Assertions.Expect(updatedRow).ToContainTextAsync("370 g", new LocatorAssertionsToContainTextOptions { Timeout = 30000 });
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-takestock-lots.zip" });
        }
    }

    [Fact(DisplayName = "Take Stock: walk page navigable + count update round-trip (J2/J4)")]
    public async Task TakeStock_WalkPage_CountUpdateRoundTrip()
    {
        var uniqueEmail = $"ts-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";
        var productName = $"TS Flour {Guid.NewGuid():N}".Substring(0, 20);

        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            // Allow up to 2 minutes for each step — E2E boots a live Aspire stack.
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── Register a fresh household ─────────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", "Take Stock E2E Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "TS E2E User");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── Create a stock-holding product ────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
            await page.WaitForURLAsync("**/Catalog/Products/Create");
            await page.FillAsync("[name='Input.Name']", productName);
            await page.SelectOptionAsync("[name='Input.DefaultUnitId']", new SelectOptionValue { Label = "g — gram" });
            await page.ClickAsync("button:has-text('Add product')");
            await page.WaitForURLAsync("**/Catalog/Products/**");

            // ── Add 500g of stock in Pantry location ──────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Pantry");
            await page.WaitForURLAsync("**/Pantry**");
            await page.ClickAsync("button:has-text('Add stock')");
            await Assertions.Expect(page.Locator("#sheet-host .sheet__panel")).ToBeVisibleAsync();

            var productSearch = page.Locator("#sheet-host .sheet__panel input[role='combobox']");
            await productSearch.FillAsync(productName);
            var productOption = page.Locator(".searchable-select__listbox li[role='option']", new() { HasText = productName });
            await Assertions.Expect(productOption).ToBeVisibleAsync();
            await productOption.ClickAsync();
            await page.FillAsync("[name='Input.Quantity']", "500");
            await page.SelectOptionAsync("[name='Input.UnitId']", new SelectOptionValue { Label = "g — gram" });
            await page.SelectOptionAsync("[name='Input.LocationId']", new SelectOptionValue { Label = "Pantry" });
            await page.ClickAsync("button:has-text('Add to pantry')");

            // Confirm stock appears.
            var pantryRow = page.Locator("tr", new() { HasText = productName });
            await Assertions.Expect(pantryRow).ToBeVisibleAsync();
            await Assertions.Expect(pantryRow).ToContainTextAsync("500 g");

            // ── Navigate directly to the Take Stock index ─────────────────────────
            await page.GotoAsync($"{BaseUrl}/pantry/take-stock");
            await page.WaitForURLAsync("**/pantry/take-stock**");
            // Verify we landed on the Take Stock page (title in document head or page body).
            await Assertions.Expect(page).ToHaveTitleAsync(new Regex("Take Stock"), new PageAssertionsToHaveTitleOptions { Timeout = 30000 });

            // The location list must render — check the catalog list is present.
            var catalogList = page.Locator(".catalog-list");
            await Assertions.Expect(catalogList).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

            // ── Open the Pantry walk page ─────────────────────────────────────────
            var pantryLink = catalogList.Locator("a.catalog-list__primary", new() { HasText = "Pantry" });
            await Assertions.Expect(pantryLink).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            await pantryLink.ClickAsync();
            await page.WaitForURLAsync("**/pantry/take-stock/**");

            // The walk page must render count rows.
            await Assertions.Expect(page.Locator(".take-stock-rows")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            await Assertions.Expect(page.Locator(".take-stock-row__name", new() { HasText = productName })).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

            // ── Change count to 300 via direct Alpine state mutation ──────────────
            // FillAsync alone does not reliably trigger Alpine's @input handler in Playwright
            // Chromium (the synthetic input event from CDP may not be seen by Alpine's listener
            // in the same tick). Wait for Alpine to initialise the component, then call
            // setCount() via Alpine.$data() — the authoritative way to drive Alpine from E2E.
            await page.WaitForFunctionAsync(@"
                () => {
                    const el = document.querySelector('[x-data]');
                    const data = el && window.Alpine && window.Alpine.$data(el);
                    return data && typeof data.setCount === 'function';
                }
            ");
            await page.EvaluateAsync(@"
                () => {
                    const el = document.querySelector('[x-data]');
                    const data = window.Alpine.$data(el);
                    const firstPid = Object.keys(data.rows)[0];
                    data.setCount(firstPid, 300);
                }
            ");

            // Save bar appears when the row is dirty (proves Alpine working-set client).
            await Assertions.Expect(page.Locator(".take-stock-savebar")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            await Assertions.Expect(page.Locator(".take-stock-savebar")).ToContainTextAsync("unsaved", new LocatorAssertionsToContainTextOptions { Timeout = 30000 });

            // ── Tap Save ──────────────────────────────────────────────────────────
            await page.ClickAsync(".take-stock-savebar button:has-text('Save')");

            // Toast confirms save (proves POST Save handler worked).
            await Assertions.Expect(page.Locator(".take-stock-toast")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            await Assertions.Expect(page.Locator(".take-stock-toast")).ToContainTextAsync("updated", new LocatorAssertionsToContainTextOptions { Timeout = 30000 });

            // Save bar hidden (no more dirty rows).
            await Assertions.Expect(page.Locator(".take-stock-savebar")).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 30000 });

            // ── Verify the Pantry reflects the new count ──────────────────────────
            await page.GotoAsync($"{BaseUrl}/Pantry");
            await page.WaitForURLAsync("**/Pantry**");
            var updatedRow = page.Locator("tr", new() { HasText = productName });
            await Assertions.Expect(updatedRow).ToContainTextAsync("300 g", new LocatorAssertionsToContainTextOptions { Timeout = 30000 });
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-takestock.zip" });
        }
    }
}
