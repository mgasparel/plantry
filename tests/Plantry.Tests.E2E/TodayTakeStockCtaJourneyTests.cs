using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E journey test for the Take Stock onboarding CTA on Today (P4-9, J6).
///
/// Journey under test:
///   Register a fresh household → Today shows the Take Stock CTA in the cold-start welcome →
///   click CTA → land on /pantry/take-stock (additive walk) →
///   add stock via the Pantry page →
///   Today no longer shows the CTA.
///
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class TodayTakeStockCtaJourneyTests(AppHostFixture appHost) : IAsyncLifetime
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

    [Fact(DisplayName = "Today: fresh household sees Take Stock CTA → lands in walk; after adding stock CTA is gone (J6/P4-9)")]
    public async Task Today_TakeStockCta_AppearsForFreshHouseholdAndRecedesOnceStockAdded()
    {
        var uniqueEmail = $"today-cta-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";
        var productName = $"CTA Test {Guid.NewGuid():N}".Substring(0, 20);

        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── Register a fresh household (empty pantry) ─────────────────────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", "CTA Journey Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "CTA Journey User");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── Step 1: Today shows the Take Stock CTA in the cold-start welcome ──
            // The cold-start welcome step 1 now links to /pantry/take-stock.
            var ctaLink = page.Locator(".today-welcome__step--cta");
            await Assertions.Expect(ctaLink).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            await Assertions.Expect(ctaLink).ToContainTextAsync("Take Stock", new LocatorAssertionsToContainTextOptions { Timeout = 10000 });
            await Assertions.Expect(ctaLink).ToHaveAttributeAsync("href", "/pantry/take-stock");

            // ── Step 2: Click the CTA → land on Take Stock index (additive walk) ──
            await ctaLink.ClickAsync();
            await page.WaitForURLAsync("**/pantry/take-stock**");

            // Verify we landed on the Take Stock location-list page.
            await Assertions.Expect(page.Locator(".catalog-list")).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

            // ── Step 3: Add stock via the Pantry add-stock sheet ─────────────────
            // First create a product.
            await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
            await page.WaitForURLAsync("**/Catalog/Products/Create");
            await page.FillAsync("[name='Input.Name']", productName);
            await page.SelectOptionAsync("[name='Input.DefaultUnitId']", new SelectOptionValue { Label = "g — gram" });
            await page.ClickAsync("button:has-text('Add product')");
            await page.WaitForURLAsync("**/Catalog/Products/**");

            // Then add stock for it.
            await page.GotoAsync($"{BaseUrl}/Pantry");
            await page.WaitForURLAsync("**/Pantry**");
            await page.ClickAsync("button:has-text('Add stock')");
            await Assertions.Expect(page.Locator("#sheet-host .sheet__panel")).ToBeVisibleAsync();
            var productSearch = page.Locator("#sheet-host .sheet__panel input[role='combobox']");
            await productSearch.FillAsync(productName);
            var productOption = page.Locator(".searchable-select__listbox li[role='option']", new() { HasText = productName });
            await Assertions.Expect(productOption).ToBeVisibleAsync();
            await productOption.ClickAsync();
            await page.FillAsync("[name='Input.Quantity']", "100");
            await page.SelectOptionAsync("[name='Input.UnitId']", new SelectOptionValue { Label = "g — gram" });
            await page.SelectOptionAsync("[name='Input.LocationId']", new SelectOptionValue { Label = "Pantry" });
            await page.ClickAsync("button:has-text('Add to pantry')");

            // Confirm stock appears in Pantry.
            var pantryRow = page.Locator("tr", new() { HasText = productName });
            await Assertions.Expect(pantryRow).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

            // ── Step 4: Revisit Today — CTA must be gone (stock now exists) ───────
            await page.GotoAsync($"{BaseUrl}/Today");
            await page.WaitForURLAsync("**/Today**");
            await page.Locator(".today-wrap").WaitForAsync();

            // The cold-start welcome is entirely hidden (household now has stock).
            var coldStartWelcome = page.Locator(".today-welcome");
            await Assertions.Expect(coldStartWelcome).ToBeHiddenAsync(
                new LocatorAssertionsToBeHiddenOptions { Timeout = 30000 });

            // The Take Stock CTA widget in the board rail is also gone.
            var ctaWidget = page.Locator("#today-take-stock-cta");
            await Assertions.Expect(ctaWidget).ToBeHiddenAsync(
                new LocatorAssertionsToBeHiddenOptions { Timeout = 10000 });
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-today-take-stock-cta.zip" });
        }
    }
}
