using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E regression test for plantry-ibuq: the Shopping list page (/Shopping) exhibited
/// unwanted horizontal scroll at mobile viewport widths. Several .sl-* clusters (the
/// .sl-summary stat box, the inline quantity editor, the add-item footer, the checked-off
/// header) were flex:none / white-space:nowrap with no allowance to shrink or wrap at phone
/// widths, so the page's content width could exceed the viewport.
///
/// Exercises the acceptance criterion directly:
/// <c>document.documentElement.scrollWidth &lt;= clientWidth</c> at 320px, 390px and 430px,
/// across every state called out in the design doc: empty list, populated list, a row with
/// the quantity editor open, a row with the note editor open, and a row with the recategorize
/// menu open (position:absolute — confirmed it does not push the document either).
///
/// Uses the mobile-viewport-fixture pattern established by MealPlanRailBreakpointE2ETests
/// (SetViewportSizeAsync mid-test rather than only at context creation).
///
/// Boots the full Aspire stack via AppHostFixture.
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class ShoppingListMobileOverflowE2ETests(AppHostFixture appHost) : IAsyncLifetime
{
    // 320/390/430px per the design doc's acceptance criterion (small phone / iPhone / large phone).
    private static readonly int[] MobileWidths = [320, 390, 430];
    private const int ViewportHeight = 844;

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

    [Fact(DisplayName = "Shopping list: no horizontal scroll at 320/390/430px — empty, populated, qty editor, note editor, recategorize menu")]
    public async Task ShoppingList_MobileWidths_NoHorizontalScrollInAnyState()
    {
        var uniqueEmail = $"e2e-slscroll-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";
        var itemName = $"Overflow Item {Guid.NewGuid():N}"[..24];
        var categoryName = $"Cat {Guid.NewGuid():N}"[..12];

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = MobileWidths[1], Height = ViewportHeight }
        });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── Register a fresh household ─────────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", "Overflow Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "Overflow User");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // A fresh household has no catalog categories, so the recategorize affordance
            // (shown only when categoryOptions.Count > 0) needs one to exist — create it
            // through the real UI rather than seeding around it.
            await page.GotoAsync($"{BaseUrl}/Catalog/Categories");
            await page.WaitForURLAsync("**/Catalog/Categories**");
            await page.FillAsync("[name='Input.Name']", categoryName);
            await page.ClickAsync("button:has-text('Create category')");
            await Assertions.Expect(page.Locator("body")).ToContainTextAsync(categoryName);

            // ── Empty-list state, checked at every width ────────────────────────────
            foreach (var width in MobileWidths)
            {
                await page.SetViewportSizeAsync(width, ViewportHeight);
                await page.GotoAsync($"{BaseUrl}/Shopping");
                await page.WaitForURLAsync("**/Shopping**");
                await Assertions.Expect(page.Locator(".sl-wrap")).ToBeVisibleAsync();
                await AssertNoHorizontalScrollAsync(page, $"empty list @ {width}px");
            }

            // ── Add a free-text item (unresolved product → no category → recat button
            //    is offered) so the populated-list and per-row-editor states exist ──────
            await page.FillAsync("[name='Input.FreeText']", itemName);
            await page.ClickAsync("#add-item-form button[type=submit]:has-text('Add to list')");
            await Assertions.Expect(page.Locator("#shopping-list")).ToContainTextAsync(itemName);

            var itemRow = page.Locator(".sl-item", new() { HasText = itemName });
            await Assertions.Expect(itemRow).ToBeVisibleAsync();

            foreach (var width in MobileWidths)
            {
                await page.SetViewportSizeAsync(width, ViewportHeight);

                // Populated list.
                await AssertNoHorizontalScrollAsync(page, $"populated list @ {width}px");

                // Quantity editor open (x-show/x-cloak — invisible at rest, so a static DOM
                // read of the page would miss its layout contribution).
                await itemRow.Locator(".sl-qty").ClickAsync();
                await Assertions.Expect(itemRow.Locator(".sl-qtyedit")).ToBeVisibleAsync();
                await AssertNoHorizontalScrollAsync(page, $"quantity editor open @ {width}px");
                await page.Keyboard.PressAsync("Escape");
                await Assertions.Expect(itemRow.Locator(".sl-qtyedit")).Not.ToBeVisibleAsync();

                // Note editor open (pencil button, scoped to .sl-actions — a bare ".sl-act" also
                // matches the hidden .sl-noterow's "Save note" submit button, which shares the
                // .sl-act class and sits earlier in the DOM, so ".First" without the .sl-actions
                // scope resolves to that hidden button instead). .sl-actions is opacity:0 at rest
                // (revealed on hover/.menu-open) — Playwright's actionability check treats that as
                // "not visible", so force the click through rather than chasing the hover-reveal
                // affordance, which is not what this test is exercising.
                await itemRow.Locator(".sl-actions .sl-act").First.ClickAsync(new LocatorClickOptions { Force = true });
                await Assertions.Expect(itemRow.Locator(".sl-noterow")).ToBeVisibleAsync();
                await AssertNoHorizontalScrollAsync(page, $"note editor open @ {width}px");
                await page.Keyboard.PressAsync("Escape");
                await Assertions.Expect(itemRow.Locator(".sl-noterow")).Not.ToBeVisibleAsync();

                // Recategorize menu open (position:absolute — confirm it does not widen the
                // document either, not just that it visually fits inside the row).
                var recatButton = itemRow.Locator("button[aria-label*='to a category']");
                await Assertions.Expect(recatButton).ToBeAttachedAsync();
                await recatButton.ClickAsync(new LocatorClickOptions { Force = true });
                await Assertions.Expect(itemRow.Locator(".sl-recat-menu")).ToBeVisibleAsync();
                await AssertNoHorizontalScrollAsync(page, $"recategorize menu open @ {width}px");
                await page.Keyboard.PressAsync("Escape");
                await Assertions.Expect(itemRow.Locator(".sl-recat-menu")).Not.ToBeVisibleAsync();
            }
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-shopping-mobile-overflow.zip" });
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts the acceptance criterion verbatim: the document's scrollable width does not
    /// exceed its visible (viewport) width — i.e. no horizontal scrollbar. On failure, names
    /// every element whose right edge pokes past the viewport (the same diagnostic the design
    /// doc's own repro script runs), so a regression here points straight at the offender
    /// instead of just reporting the aggregate scrollWidth/clientWidth mismatch.
    /// </summary>
    private static async Task AssertNoHorizontalScrollAsync(IPage page, string stateDescription)
    {
        var widths = await page.EvaluateAsync<int[]>(
            "() => [document.documentElement.scrollWidth, document.documentElement.clientWidth]");
        var scrollWidth = widths[0];
        var clientWidth = widths[1];
        if (scrollWidth > clientWidth)
        {
            var offenders = await page.EvaluateAsync<string>(@"() => {
                const vw = document.documentElement.clientWidth;
                return [...document.querySelectorAll('*')]
                    .filter(el => el.getBoundingClientRect().right > vw + 1)
                    .map(el => `${el.tagName}#${el.id}.${[...el.classList].join('.')} right=${Math.round(el.getBoundingClientRect().right)}`)
                    .join(', ');
            }");
            throw new Xunit.Sdk.XunitException(
                $"Horizontal scroll detected ({stateDescription}): scrollWidth={scrollWidth} > clientWidth={clientWidth}. Offenders: {offenders}");
        }
    }
}
