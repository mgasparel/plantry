using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;
using Aspire.Hosting.Testing;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E smoke test (Playwright) — the Slice 2 done-when journey (PHASE-1-PLAN.md):
///   "manual add a product's stock → see it in pantry with correct aggregated qty/expiry →
///    consume some → see remaining qty + a journal entry."
///
/// Boots the whole service graph from the Aspire AppHost via AppHostFixture.
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class StockSmokeTests(AppHostFixture appHost) : IAsyncLifetime
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

    [Fact(DisplayName = "Add stock → see in pantry → consume → see remaining + journal")]
    public async Task AddStockConsumeSeeRemaining()
    {
        var uniqueEmail = $"smoke-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";
        var productName = $"Smoke Flour {Guid.NewGuid():N}".Substring(0, 22);

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── Register a household (lands on Today home, logged in) ─────────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", "Smoke Stock Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "Smoke User");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── Create a stock-holding product ───────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
            await page.WaitForURLAsync("**/Catalog/Products/Create");
            await page.FillAsync("[name='Input.Name']", productName);
            await page.SelectOptionAsync("[name='Input.DefaultUnitId']", new SelectOptionValue { Label = "g — gram" });
            await page.ClickAsync("button:has-text('Add product')");
            await page.WaitForURLAsync("**/Catalog/Products/**");

            // ── Add stock via the htmx sheet on the Pantry ───────────────────────
            // Scope to #sheet-host to avoid strict-mode collision with the global
            // quick-add sheet in _Layout (both use .sheet/.sheet__panel classes).
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

            // ── See it in the pantry with the right aggregated quantity ──────────
            var row = page.Locator("tr", new() { HasText = productName });
            await Assertions.Expect(row).ToBeVisibleAsync();
            await Assertions.Expect(row).ToContainTextAsync("500 g");

            // ── Open the product detail and consume part of it ──────────────────
            await page.ClickAsync($"a.data-grid__link:has-text('{productName}')");
            await page.WaitForURLAsync("**/Pantry/Products/Detail/**");
            await Assertions.Expect(page.Locator(".pantry-detail__total")).ToContainTextAsync("500 g");

            await page.ClickAsync("button:has-text('Consume')");
            await Assertions.Expect(page.Locator("#sheet-host .sheet__panel")).ToBeVisibleAsync();
            await page.FillAsync("[name='Input.Amount']", "200");
            await page.SelectOptionAsync("[name='Input.UnitId']", new SelectOptionValue { Label = "g — gram" });
            await page.ClickAsync("button:has-text('Confirm')");

            // ── Remaining quantity reflects the consume, and a journal row appears ──
            await Assertions.Expect(page.Locator("#stock-detail")).ToContainTextAsync("300 g");
            await Assertions.Expect(page.Locator("#stock-detail").GetByText("Consumed")).ToBeVisibleAsync();
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-stock.zip" });
        }
    }
}
