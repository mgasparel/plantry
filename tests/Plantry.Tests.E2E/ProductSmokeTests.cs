using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;
using Aspire.Hosting.Testing;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E smoke tests (Playwright).
///
/// Slice 1 done-when journey:
///   "create a product with a SKU and a conversion, edit it, and archive it"
///
/// Boots the whole service graph from the Aspire AppHost via AppHostFixture —
/// no manually started app instance required.
///
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class ProductSmokeTests(AppHostFixture appHost) : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    private string BaseUrl => appHost.BaseUrl;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }

    [Fact(DisplayName = "Create product with SKU and conversion → edit → archive")]
    public async Task CreateEditArchiveProduct()
    {
        var uniqueEmail = $"smoke-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";
        var productName = $"Smoke Oat Milk {Guid.NewGuid():N}".Substring(0, 24);
        var renamedProductName = $"{productName} (renamed)";

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true
        });

        await context.Tracing.StartAsync(new()
        {
            Screenshots = true,
            Snapshots = true,
            Sources = true
        });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── Step 1: Register a household (lands on Pantry, logged in) ─────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");

            await page.FillAsync("[name='Input.HouseholdName']", "Smoke Catalog Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "Smoke User");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Pantry**");

            // ── Step 2: Navigate to Catalog → Products → Create ───────────────────
            await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
            await page.WaitForURLAsync("**/Catalog/Products/Create");

            await page.FillAsync("[name='Input.Name']", productName);
            await page.SelectOptionAsync("[name='Input.DefaultUnitId']", new SelectOptionValue { Label = "g — gram" });
            await page.ClickAsync("button:has-text('Add product')");

            // ── Step 3: Redirected to Detail; product name shown ──────────────────
            await page.WaitForURLAsync("**/Catalog/Products/Detail/**");
            var heading = await page.Locator(".catalog-section__heading").First.TextContentAsync();
            Assert.Contains(productName, heading);

            // ── Step 4: Add a SKU ──────────────────────────────────────────────────
            await page.FillAsync("[name='SkuInput.Label']", "1 kg bag");
            await page.FillAsync("[name='SkuInput.SizeQuantity']", "1");
            await page.SelectOptionAsync("[name='SkuInput.SizeUnitId']", new SelectOptionValue { Label = "kg — kilogram" });
            await page.ClickAsync("button:has-text('Add SKU')");

            await page.WaitForURLAsync("**/Catalog/Products/Detail/**");
            await Assertions.Expect(page.Locator(".catalog-list__primary", new() { HasText = "1 kg bag" })).ToBeVisibleAsync();

            // ── Step 5: Add a conversion (cup → gram, density-style) ──────────────
            await page.SelectOptionAsync("[name='ConversionInput.FromUnitId']", new SelectOptionValue { Label = "cup — cup" });
            await page.SelectOptionAsync("[name='ConversionInput.ToUnitId']", new SelectOptionValue { Label = "g — gram" });
            await page.FillAsync("[name='ConversionInput.Factor']", "120");
            await page.ClickAsync("button:has-text('Add conversion')");

            await page.WaitForURLAsync("**/Catalog/Products/Detail/**");
            await Assertions.Expect(page.Locator(".catalog-list__primary", new() { HasText = "1 cup = 120 g" })).ToBeVisibleAsync();

            // ── Step 6: Edit the product (rename) ─────────────────────────────────
            await page.ClickAsync("a:has-text('Edit')");
            await page.WaitForURLAsync("**/Catalog/Products/Edit/**");

            await page.FillAsync("[name='Input.Name']", renamedProductName);
            await page.ClickAsync("button:has-text('Save changes')");

            await page.WaitForURLAsync("**/Catalog/Products/Detail/**");
            var renamedHeading = await page.Locator(".catalog-section__heading").First.TextContentAsync();
            Assert.Contains(renamedProductName, renamedHeading);

            // ── Step 7: Archive it ─────────────────────────────────────────────────
            await page.ClickAsync("button:has-text('Archive')");
            await page.WaitForURLAsync("**/Catalog/Products/Detail/**");
            await Assertions.Expect(page.Locator(".catalog-list__meta", new() { HasText = "archived" })).ToBeVisibleAsync();

            // ── Step 8: Archived product disappears from the active list ──────────
            await page.GotoAsync($"{BaseUrl}/Catalog/Products");
            await page.WaitForURLAsync("**/Catalog/Products");
            await Assertions.Expect(page.Locator(".catalog-list__primary", new() { HasText = renamedProductName })).Not.ToBeVisibleAsync();
        }
        finally
        {
            await context.Tracing.StopAsync(new()
            {
                Path = "trace-product.zip"
            });
        }
    }
}
