using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E smoke test for the expiring-soon widget on the Today (Home) page (SPEC Page 0 §0d,
/// plantry-81x acceptance criteria):
///
///   "A household with an expiring lot sees it on Home."
///
/// Journey:
///   Register a fresh household → create a product → add stock with an expiry date within 7 days
///   → navigate to Today → verify the expiring-soon widget shows the product name and a day badge.
///
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class TodayExpiringSoonSmokeTests(AppHostFixture appHost) : IAsyncLifetime
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

    [Fact(DisplayName = "Today: household with expiring stock sees it in the expiring-soon widget (plantry-81x/AC1)")]
    public async Task Today_ExpiringSoon_ShowsProductWithExpiryInWidget()
    {
        var uniqueEmail = $"expiring-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";
        var productName = $"ExpirySmoke {Guid.NewGuid():N}".Substring(0, 20);

        // Expiry date: 3 days from now (well within the 7-day window)
        var expiryDate = DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-dd");

        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── 1. Register a fresh household ─────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", "Expiry Test Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "Expiry Tester");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── 2. Create a product ───────────────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
            await page.WaitForURLAsync("**/Catalog/Products/Create");
            await page.FillAsync("[name='Input.Name']", productName);
            await page.SelectOptionAsync("[name='Input.DefaultUnitId']", new SelectOptionValue { Label = "g — gram" });
            await page.ClickAsync("button:has-text('Add product')");
            await page.WaitForURLAsync("**/Catalog/Products/**");

            // ── 3. Add stock with an expiry date within the window ────────────────
            await page.GotoAsync($"{BaseUrl}/Pantry");
            await page.WaitForURLAsync("**/Pantry**");
            await page.ClickAsync("button:has-text('Add stock')");
            await Assertions.Expect(page.Locator("#sheet-host .sheet__panel")).ToBeVisibleAsync();

            var productSearch = page.Locator("#sheet-host .sheet__panel input[role='combobox']");
            await productSearch.FillAsync(productName);
            var productOption = page.Locator(".searchable-select__listbox li[role='option']", new() { HasText = productName });
            await Assertions.Expect(productOption).ToBeVisibleAsync();
            await productOption.ClickAsync();

            await page.FillAsync("[name='Input.Quantity']", "250");
            await page.SelectOptionAsync("[name='Input.UnitId']", new SelectOptionValue { Label = "g — gram" });
            await page.SelectOptionAsync("[name='Input.LocationId']", new SelectOptionValue { Label = "Pantry" });
            await page.FillAsync("[name='Input.ExpiryDate']", expiryDate);
            await page.ClickAsync("button:has-text('Add to pantry')");

            // Confirm the stock row appears in the Pantry table
            var pantryRow = page.Locator("tr", new() { HasText = productName });
            await Assertions.Expect(pantryRow).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

            // ── 4. Navigate to Today and verify expiring-soon widget ───────────────
            await page.GotoAsync($"{BaseUrl}/Today");
            await page.WaitForURLAsync("**/Today**");
            await page.Locator(".today-wrap").WaitForAsync();

            // The expiring-soon widget must be visible (not cold-start, not all-clear)
            var widget = page.Locator(".today-exp-widget");
            await Assertions.Expect(widget).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

            // The product name must appear in the expiring list
            var expRow = page.Locator(".today-exp-row", new() { HasText = productName });
            await Assertions.Expect(expRow).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });

            // A day-pill badge is present — the unified expiry pill (plantry-fdoq); its relative
            // wording could be "Today", "Tomorrow", "in Nd", or "Expired Nd ago".
            var dayPill = expRow.Locator(".badge-expiry");
            await Assertions.Expect(dayPill).ToBeVisibleAsync();

            // The widget's count badge is > 0 (not calm/greyed-out for zero items)
            var countBadge = widget.Locator(".today-count-badge");
            await Assertions.Expect(countBadge).ToBeVisibleAsync();
            var badgeText = await countBadge.TextContentAsync();
            Assert.NotEqual("0", badgeText?.Trim());

            // The populated foot offers both actions: the secondary "Review all in pantry"
            // link and the primary "Use these up" deep-link into the Recipes browse.
            var pantryLink = widget.Locator(".today-widget__foot a", new() { HasText = "pantry" });
            await Assertions.Expect(pantryLink).ToBeVisibleAsync();

            // "Use these up" deep-links to the Recipes browse pre-filtered to "use soon"
            // (plantry-w1e). Confirm the link target carries the soon filter.
            var useUpLink = widget.Locator(".today-widget__foot a.today-widget__foot-cta");
            await Assertions.Expect(useUpLink).ToBeVisibleAsync();
            await Assertions.Expect(useUpLink).ToContainTextAsync("Use these up");
            await Assertions.Expect(useUpLink).ToHaveAttributeAsync("href", "/Recipes?soon=true");

            // Following it lands on the Recipes browse with the "Use soon" filter active —
            // the deep-link landing on the correctly-filtered browse (plantry-w1e AC).
            await useUpLink.ClickAsync();
            await page.WaitForURLAsync("**/Recipes**");
            await page.Locator(".recipes-browse-wrap").WaitForAsync();

            // The "Use soon" toggle renders in its active (warning) state, proving the
            // browse arrived with UseSoon bound on from the deep link.
            var activeSoonChip = page.Locator(".filter-chip.filter-chip--warning.is-active", new() { HasText = "Use soon" });
            await Assertions.Expect(activeSoonChip).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-today-expiring-soon.zip" });
        }
    }
}
