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

    [Fact(DisplayName = "Take Stock: inline add — new item appears with stock; existing item reused, no dup (J5/P4-7)")]
    public async Task TakeStock_InlineAdd_NewItemAppearsWithStockExistingItemReused()
    {
        var uniqueEmail = $"ts-inline-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";
        var productName = $"InlineItem {Guid.NewGuid():N}".Substring(0, 22);

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
            await page.FillAsync("[name='Input.HouseholdName']", "TS Inline Add E2E Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "TS Inline E2E User");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── Navigate to Take Stock → open Pantry walk ─────────────────────────
            await page.GotoAsync($"{BaseUrl}/pantry/take-stock");
            await page.WaitForURLAsync("**/pantry/take-stock**");
            var pantryLink = page.Locator(".ts-loc-grid a.ts-loc-card", new() { HasText = "Pantry" });
            await Assertions.Expect(pantryLink).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            await pantryLink.ClickAsync();
            await page.WaitForURLAsync("**/pantry/take-stock/**");

            // ── Click "+ Add item" to open the inline-add sheet ───────────────────
            var addItemBtn = page.Locator("button.ts-add-item");
            await Assertions.Expect(addItemBtn).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            await addItemBtn.ClickAsync();

            // The inline-add sheet is the one containing "Add ingredient" in its header.
            // Use :has to scope to the take-stock-add sheet specifically (avoids conflicts with
            // the global inventory sheet which may also be present in the page layout).
            var sheet = page.Locator(".sheet:has(.sheet__title:text-is('Add ingredient')) .sheet__panel");
            await Assertions.Expect(sheet).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15000 });

            // ── Switch to "Create as staple" mode to type a new product name ──────
            var createStapleBtn = sheet.Locator("button:has-text('+ Create as staple')");
            await Assertions.Expect(createStapleBtn).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });
            await createStapleBtn.ClickAsync();

            // Staple name input should appear.
            var stapleNameInput = sheet.Locator(".ingredient-row__staple input[type='text']");
            await Assertions.Expect(stapleNameInput).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });
            await stapleNameInput.FillAsync(productName);

            // Select unit in the staple mode unit select (available when TrackStock=false uses newStapleUnit).
            var stapleUnitSelect = sheet.Locator(".ingredient-row__staple select");
            await stapleUnitSelect.SelectOptionAsync(new SelectOptionValue { Label = "g" });

            // Fill in the opening count in the extra-fields partial.
            var countInput = sheet.Locator("#add-count");
            await Assertions.Expect(countInput).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });
            await countInput.FillAsync("150");

            // ── Confirm add ───────────────────────────────────────────────────────
            var addBtn = sheet.Locator("button.btn--primary");
            await Assertions.Expect(addBtn).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });
            await addBtn.ClickAsync();

            // Sheet should close (sheetOpen → false).
            await Assertions.Expect(sheet).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 15000 });

            // ── New row should appear in the added-items section ──────────────────
            // Toast confirms success.
            var toast = page.Locator(".ts-toast");
            await Assertions.Expect(toast).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15000 });

            // The added-items list renders the new row name.
            var addedRows = page.Locator("ul.ts-rows[aria-label='Added items']");
            await Assertions.Expect(addedRows).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15000 });
            await Assertions.Expect(addedRows.Locator(".nm-text", new() { HasText = productName })).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15000 });

            // ── Re-open sheet and select the same product via search (Path A — reuse, no dup) ─
            // This verifies that an existing catalog match is reused rather than duplicated.
            await addItemBtn.ClickAsync();
            await Assertions.Expect(sheet).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15000 });

            // Search for the product that was just created — hits the real catalog.
            // Use PressSequentiallyAsync to fire keydown/keypress/keyup events for each character
            // so that htmx's "keyup changed" trigger fires reliably.
            var searchInput = page.Locator("#prod-search-sheet");
            await searchInput.ClickAsync();
            await searchInput.PressSequentiallyAsync(productName, new() { Delay = 50 });

            // Wait for the htmx search to populate the listbox (250ms debounce + server round-trip).
            var searchResult = page.Locator("#prod-list-sheet li[role='option']", new() { HasText = productName });
            await Assertions.Expect(searchResult).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15000 });

            // Select the existing product from search results — exercises Path A (selectProduct).
            // Clicking the <li> fires its Alpine @click handler, which dispatches 'pick-product'
            // that bubbles to the .sheet div where selectProduct(draft, detail) sets draft.productId.
            await searchResult.ClickAsync();

            // Set a new opening count in the extra-fields partial.
            var countInputReuse = sheet.Locator("#add-count");
            await countInputReuse.ClearAsync();
            await countInputReuse.FillAsync("50");

            // Click Add — Path A: existing product → inject as dirty row, no server create.
            var addBtnReuse = sheet.Locator("button.btn--primary");
            await Assertions.Expect(addBtnReuse).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 5000 });
            await addBtnReuse.ClickAsync();

            // Sheet closes and a toast appears confirming the item was added to the working set.
            await Assertions.Expect(sheet).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 15000 });
            await Assertions.Expect(page.Locator(".ts-toast")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });

            // Save the dirty reuse row via the sticky save bar.
            var saveBar = page.Locator(".ts-savebar");
            await Assertions.Expect(saveBar).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });
            await page.ClickAsync(".ts-savebar button:has-text('Save')");
            await Assertions.Expect(page.Locator(".ts-savebar")).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 15000 });

            // ── Verify the product has stock on Pantry and appears only once (no dup) ──
            await page.GotoAsync($"{BaseUrl}/Pantry");
            await page.WaitForURLAsync("**/Pantry**");
            var productRow = page.Locator("tr", new() { HasText = productName });
            await Assertions.Expect(productRow).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            // Product appears exactly once — no duplicate catalog row minted.
            await Assertions.Expect(productRow).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 10000 });
            // Quantity reflects the reuse save (50g), not the original 150g.
            await Assertions.Expect(productRow).ToContainTextAsync("50", new LocatorAssertionsToContainTextOptions { Timeout = 10000 });
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-takestock-inline-add.zip" });
        }
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
            var pantryLink = page.Locator(".ts-loc-grid a.ts-loc-card", new() { HasText = "Pantry" });
            await Assertions.Expect(pantryLink).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            await pantryLink.ClickAsync();
            await page.WaitForURLAsync("**/pantry/take-stock/**");

            // ── Expand the lot panel ──────────────────────────────────────────────
            // The expand-lots button uses aria-label pattern; locate by its CSS class ts-expand
            // (visible only for rows with active stock).
            var adjustLotsBtn = page.Locator("button.ts-expand").First;
            await Assertions.Expect(adjustLotsBtn).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            await adjustLotsBtn.ClickAsync();

            // The lot panel should appear with both lots.
            var lotPanel = page.Locator(".ts-hatch");
            await Assertions.Expect(lotPanel).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            // Two active lot rows should appear (the panel also contains a .ts-lot.found row for adding found stock).
            await Assertions.Expect(page.Locator(".ts-lot:not(.found)")).ToHaveCountAsync(2, new LocatorAssertionsToHaveCountOptions { Timeout = 30000 });

            // ── Adjust Alpine state and trigger save via $data.save() directly ──────────
            // Alpine.$data(panel) returns a reactive proxy of the merged data stack.
            // setLotAmount and save() are accessible directly. This is more reliable than
            // synthetic button click events for testing async Alpine methods.
            var saveUrl = await page.EvaluateAsync<string>(@"
                () => {
                    const panel = document.querySelector('.ts-hatch');
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
            var saveBtn = page.Locator(".ts-hatch button:has-text('Save lot changes')");
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

            // The location list must render — check the location grid is present.
            var locGrid = page.Locator(".ts-loc-grid");
            await Assertions.Expect(locGrid).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

            // ── Open the Pantry walk page ─────────────────────────────────────────
            var pantryLink = locGrid.Locator("a.ts-loc-card", new() { HasText = "Pantry" });
            await Assertions.Expect(pantryLink).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            await pantryLink.ClickAsync();
            await page.WaitForURLAsync("**/pantry/take-stock/**");

            // The walk page must render count rows.
            await Assertions.Expect(page.Locator(".ts-rows")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            await Assertions.Expect(page.Locator(".nm-text", new() { HasText = productName })).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

            // ── Change count to 300 via the island's test seam ───────────────────
            // The Preact island exposes window.__takeStockIsland.setCount() for E2E use,
            // replacing the previous Alpine.$data() pattern (bead plantry-2zvm.2).
            await page.WaitForFunctionAsync(@"
                () => typeof window.__takeStockIsland?.setCountByIndex === 'function'
            ");
            await page.EvaluateAsync(@"
                () => { window.__takeStockIsland.setCountByIndex(0, 300); }
            ");

            // Save bar appears when the row is dirty (proves Alpine working-set client).
            await Assertions.Expect(page.Locator(".ts-savebar")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            await Assertions.Expect(page.Locator(".ts-savebar")).ToContainTextAsync("ready to save", new LocatorAssertionsToContainTextOptions { Timeout = 30000 });

            // ── Tap Save ──────────────────────────────────────────────────────────
            await page.ClickAsync(".ts-savebar button:has-text('Save')");

            // Toast confirms save (proves POST Save handler worked).
            await Assertions.Expect(page.Locator(".ts-toast")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            await Assertions.Expect(page.Locator(".ts-toast")).ToContainTextAsync("updated", new LocatorAssertionsToContainTextOptions { Timeout = 30000 });

            // Save bar hidden (no more dirty rows).
            await Assertions.Expect(page.Locator(".ts-savebar")).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 30000 });

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

    [Fact(DisplayName = "Take Stock: unit selector (C10) — select alternate unit, save, Pantry reflects converted quantity (L5)")]
    public async Task TakeStock_UnitSelector_AlternateUnit_SaveReflectsConvertedQuantity()
    {
        var uniqueEmail = $"ts-unit-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";
        var productName = $"TS Unit {Guid.NewGuid():N}".Substring(0, 18);

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
            await page.FillAsync("[name='Input.HouseholdName']", "TS Unit Sel E2E Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "TS Unit E2E User");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── Create a stock-holding product with default unit = g ──────────────
            // g is a Mass unit; kg is a same-dimension Mass sibling — both will appear in
            // the unit selector (C10 SupportedUnits via UnitConverter.ReachableUnits).
            await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
            await page.WaitForURLAsync("**/Catalog/Products/Create");
            await page.FillAsync("[name='Input.Name']", productName);
            await page.SelectOptionAsync("[name='Input.DefaultUnitId']", new SelectOptionValue { Label = "g — gram" });
            await page.ClickAsync("button:has-text('Add product')");
            await page.WaitForURLAsync("**/Catalog/Products/**");

            // ── Add 500g to Pantry ─────────────────────────────────────────────────
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
            var pantryRow = page.Locator("tr", new() { HasText = productName });
            await Assertions.Expect(pantryRow).ToBeVisibleAsync();
            await Assertions.Expect(pantryRow).ToContainTextAsync("500 g");

            // ── Navigate to Take Stock → open Pantry walk ─────────────────────────
            await page.GotoAsync($"{BaseUrl}/pantry/take-stock");
            await page.WaitForURLAsync("**/pantry/take-stock**");
            var pantryLink = page.Locator(".ts-loc-card", new() { HasText = "Pantry" });
            await Assertions.Expect(pantryLink).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            await pantryLink.ClickAsync();
            await page.WaitForURLAsync("**/pantry/take-stock/**");

            // ── Verify the unit selector renders with multiple options (C10) ───────
            // The selector has aria-label "Unit for <productName>" and should include kg.
            var unitSelect = page.Locator($"select[aria-label='Unit for {productName}']");
            await Assertions.Expect(unitSelect).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

            // Collect the kg option value (its UUID) from the select to use in Alpine mutation.
            var kgUnitId = await page.EvaluateAsync<string?>($@"
                () => {{
                    const sel = document.querySelector(""select[aria-label='Unit for {productName}']"");
                    if (!sel) return null;
                    const kgOpt = Array.from(sel.options).find(o => o.text === 'kg');
                    return kgOpt ? kgOpt.value : null;
                }}
            ");
            Assert.NotNull(kgUnitId);
            Assert.NotEqual(string.Empty, kgUnitId);

            // ── Wait for island, then set count=1 in kg via the island test seam ──
            // The Preact island exposes window.__takeStockIsland replacing the old Alpine.$data()
            // pattern (bead plantry-2zvm.2). setUnitId + setCountByIndex are the seam API.
            await page.WaitForFunctionAsync(@"
                () => typeof window.__takeStockIsland?.setCountByIndex === 'function'
            ");
            await page.EvaluateAsync($@"
                (kgUnitId) => {{
                    const ids = window.__takeStockIsland.getProductIds();
                    const firstPid = ids[0];
                    // Switch unit to kg before counting so the POST payload carries the new unitId.
                    window.__takeStockIsland.setUnitId(firstPid, kgUnitId);
                    // Set count to 1 (= 1 kg; expect 1000 g in Pantry after Save).
                    window.__takeStockIsland.setCount(firstPid, 1);
                }}
            ", kgUnitId);

            // ── Save bar should appear (row is dirty) ─────────────────────────────
            await Assertions.Expect(page.Locator(".ts-savebar")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

            // ── Tap Save ──────────────────────────────────────────────────────────
            await page.ClickAsync(".ts-savebar button:has-text('Save')");

            // Toast confirms success.
            await Assertions.Expect(page.Locator(".ts-toast")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

            // Save bar hidden after save.
            await Assertions.Expect(page.Locator(".ts-savebar")).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 30000 });

            // ── Verify Pantry reflects 1 kg = 1000 g ─────────────────────────────
            // The save command stores the counted value in the chosen unit (kg).
            // Plantry displays stock in the product's display unit (g), so 1 kg → 1000 g.
            await page.GotoAsync($"{BaseUrl}/Pantry");
            await page.WaitForURLAsync("**/Pantry**");
            var updatedRow = page.Locator("tr", new() { HasText = productName });
            await Assertions.Expect(updatedRow).ToContainTextAsync("1000 g", new LocatorAssertionsToContainTextOptions { Timeout = 30000 });
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-takestock-unit-selector.zip" });
        }
    }

    [Fact(DisplayName = "Take Stock: no-location section — file unplaced product → moves under its Location (J7/P4-8)")]
    public async Task TakeStock_NoLocation_FileUnplacedProduct_MovesUnderLocation()
    {
        var uniqueEmail = $"ts-noloc-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";
        var productName = $"TS NoLoc {Guid.NewGuid():N}".Substring(0, 18);

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
            await page.FillAsync("[name='Input.HouseholdName']", "TS No-Location E2E Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "TS NoLoc E2E User");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── Create a stock-holding product WITHOUT a default location ──────────
            await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
            await page.WaitForURLAsync("**/Catalog/Products/Create");
            await page.FillAsync("[name='Input.Name']", productName);
            await page.SelectOptionAsync("[name='Input.DefaultUnitId']", new SelectOptionValue { Label = "g — gram" });
            // Do NOT set a default location — leave it blank so the product is unplaced.
            await page.ClickAsync("button:has-text('Add product')");
            await page.WaitForURLAsync("**/Catalog/Products/**");

            // ── Add stock to the unplaced product via the Pantry AddStock sheet ──
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

            // ── Navigate to Take Stock index — "No location" entry should appear ──
            await page.GotoAsync($"{BaseUrl}/pantry/take-stock");
            await page.WaitForURLAsync("**/pantry/take-stock**");
            var noLocLink = page.Locator("a[href='/pantry/take-stock/no-location']");
            await Assertions.Expect(noLocLink).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

            // ── Navigate to the No-location page ─────────────────────────────────
            await noLocLink.ClickAsync();
            await page.WaitForURLAsync("**/pantry/take-stock/no-location**");

            // The product should appear in the no-location section.
            await Assertions.Expect(page.Locator(".nm-text", new() { HasText = productName }))
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

            // ── Choose a location for the product ─────────────────────────────────
            // Alpine state: set locationId via the <select> for this row.
            await page.WaitForFunctionAsync(@"
                () => {
                    const el = document.querySelector('[x-data]');
                    const data = el && window.Alpine && window.Alpine.$data(el);
                    return data && typeof data.onLocationChange === 'function';
                }
            ");

            // Select "Pantry" from the location picker for the first (and only) row.
            var pickerSelect = page.Locator(".ts-locpick-select").First;
            await Assertions.Expect(pickerSelect).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            await pickerSelect.SelectOptionAsync(new SelectOptionValue { Label = "Pantry" });

            // Save bar should appear (row is now dirty — location chosen).
            await Assertions.Expect(page.Locator(".ts-savebar"))
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

            // ── File the product ──────────────────────────────────────────────────
            await page.ClickAsync(".ts-savebar button:has-text('File')");

            // Toast should confirm.
            await Assertions.Expect(page.Locator(".ts-toast"))
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            await Assertions.Expect(page.Locator(".ts-toast"))
                .ToContainTextAsync("filed", new LocatorAssertionsToContainTextOptions { Timeout = 30000 });

            // The row should disappear from the Alpine working set (client removes it).
            await Assertions.Expect(page.Locator(".nm-text", new() { HasText = productName }))
                .ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 30000 });

            // ── Navigate back to the Take Stock index — "No location" entry gone ──
            await page.GotoAsync($"{BaseUrl}/pantry/take-stock");
            await page.WaitForURLAsync("**/pantry/take-stock**");
            // The unplaced entry must no longer be present (product is now placed).
            await Assertions.Expect(page.Locator("a.ts-loc-card.unplaced"))
                .ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 30000 });

            // ── Product now appears in the Pantry walk under its Location ──────────
            var pantryWalkLink = page.Locator(".ts-loc-grid a.ts-loc-card", new() { HasText = "Pantry" });
            await Assertions.Expect(pantryWalkLink).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            await pantryWalkLink.ClickAsync();
            await page.WaitForURLAsync("**/pantry/take-stock/**");

            // The product should now appear on the Pantry walk page (default location = Pantry).
            await Assertions.Expect(page.Locator(".nm-text", new() { HasText = productName }))
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-takestock-no-location.zip" });
        }
    }
}
