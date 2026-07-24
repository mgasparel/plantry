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

            // Archive now guards with a native confirm() (plantry-jby4) — auto-accept so the
            // archive proceeds; Playwright otherwise dismisses dialogs (cancel), blocking the post.
            page.Dialog += async (_, dialog) => await dialog.AcceptAsync();

            // ── Step 1: Register a household (lands on Today home, logged in) ──────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");

            await page.FillAsync("[name='Input.HouseholdName']", "Smoke Catalog Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "Smoke User");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── Step 2: Navigate to Catalog → Products → Create ───────────────────
            await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
            await page.WaitForURLAsync("**/Catalog/Products/Create");

            await page.FillAsync("[name='Input.Name']", productName);
            await page.SelectOptionAsync("[name='Input.DefaultUnitId']", new SelectOptionValue { Label = "g — gram" });
            await page.ClickAsync("button:has-text('Create Product')");

            // ── Step 3: Redirected to Detail; product name shown ──────────────────
            await page.WaitForURLAsync("**/Catalog/Products/**");
            var heading = await page.Locator(".page-header__title").First.TextContentAsync();
            Assert.Contains(productName, heading);

            // Capture the product id from the Detail redirect URL — reused below to navigate
            // straight to both detail pages without depending on prior-step DOM state.
            var productId = new Uri(page.Url).Segments[^1];

            // ── Step 4: Add a SKU ──────────────────────────────────────────────────
            await page.FillAsync("[name='SkuInput.Label']", "1 kg bag");
            await page.FillAsync("[name='SkuInput.SizeQuantity']", "1");
            await page.SelectOptionAsync("[name='SkuInput.SizeUnitId']", new SelectOptionValue { Label = "kg — kilogram" });
            await page.ClickAsync("button:has-text('Add SKU')");

            await page.WaitForURLAsync("**/Catalog/Products/**");
            await Assertions.Expect(page.Locator(".catalog-list__primary", new() { HasText = "1 kg bag" })).ToBeVisibleAsync();

            // ── Step 5: Add a conversion (cup → gram, density-style) ──────────────
            await page.SelectOptionAsync("[name='ConversionInput.FromUnitId']", new SelectOptionValue { Label = "cup — cup" });
            await page.SelectOptionAsync("[name='ConversionInput.ToUnitId']", new SelectOptionValue { Label = "g — gram" });
            await page.FillAsync("[name='ConversionInput.Factor']", "120");
            await page.ClickAsync("button:has-text('Add conversion')");

            await page.WaitForURLAsync("**/Catalog/Products/**");
            await Assertions.Expect(page.Locator(".catalog-list__primary", new() { HasText = "1 cup = 120 g" })).ToBeVisibleAsync();

            // ── Step 6: Edit the product (rename) — form is inline on the merged Detail page ──
            await page.FillAsync("[name='Input.Name']", renamedProductName);
            await page.ClickAsync("button:has-text('Save changes')");

            await page.WaitForURLAsync("**/Catalog/Products/**");
            var renamedHeading = await page.Locator(".page-header__title").First.TextContentAsync();
            Assert.Contains(renamedProductName, renamedHeading);

            // ── Step 6b: Unified product list (plantry-sjfn) — the product has never been
            // stocked, so it must NOT appear in Pantry's default "In stock" scope, but must fold
            // into "Everything" scope as a quiet, never-stocked row. ────────────────────────
            await page.GotoAsync($"{BaseUrl}/Pantry");
            await page.WaitForURLAsync("**/Pantry**");
            await Assertions.Expect(page.Locator(".data-grid__link", new() { HasText = renamedProductName }))
                .Not.ToBeVisibleAsync();

            // The scope radio is visually hidden (seg-ctrl, sr-only) — click its label, as other
            // seg-ctrl E2E tests do (e.g. RecipeAuthorJourneyTests' Scale mode toggle).
            await page.Locator(".seg-ctrl__item", new() { HasText = "Everything" }).ClickAsync();
            var everythingRow = page.Locator("tr", new() { HasText = renamedProductName });
            await Assertions.Expect(everythingRow).ToBeVisibleAsync();
            await Assertions.Expect(everythingRow).ToContainTextAsync("Not stocked");
            await Assertions.Expect(everythingRow).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("data-grid__row--muted"));

            // ── Step 6c: The trailing pencil action navigates to the catalog definition form,
            // never the stock view — verified from the Everything-scope row. ─────────────────
            await everythingRow.Locator("a.data-grid__icon-action").ClickAsync();
            await page.WaitForURLAsync($"**/Catalog/Products/{productId}");

            // ── Step 6d: The name link always goes to the stock view — for a never-stocked
            // product that's the zero-stock empty-state landing (Add stock CTA, no Consume). ──
            await page.GotoAsync($"{BaseUrl}/Pantry");
            await page.WaitForURLAsync("**/Pantry**");
            // The scope radio is visually hidden (seg-ctrl, sr-only) — click its label, as other
            // seg-ctrl E2E tests do (e.g. RecipeAuthorJourneyTests' Scale mode toggle).
            await page.Locator(".seg-ctrl__item", new() { HasText = "Everything" }).ClickAsync();
            await page.Locator("tr", new() { HasText = renamedProductName }).Locator("a.data-grid__link").ClickAsync();
            await page.WaitForURLAsync($"**/Pantry/Products/Detail/{productId}");

            await Assertions.Expect(page.Locator(".page-header__subtitle", new() { HasText = "Not in stock" }))
                .ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Consume" })).Not.ToBeVisibleAsync();
            // "Add stock" appears twice on the zero-stock landing (header CTA + empty-state card
            // CTA, both per design) — .First is enough to prove the affordance is there.
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Add stock" }).First).ToBeVisibleAsync();

            // ── Step 6e: Add stock works from the zero-stock landing itself — household
            // registration seeds default locations (Fridge/Freezer/Pantry/Counter), so "Pantry" is
            // already available. Use the header's primary CTA and confirm the page flips out of
            // the empty state (subtitle, Consume button, lots row). ───────────────────────────
            await page.GotoAsync($"{BaseUrl}/Pantry/Products/Detail/{productId}");
            await page.GetByRole(AriaRole.Button, new() { Name = "Add stock" }).First.ClickAsync();
            var addStockSheet = page.Locator("#sheet-host [role=dialog][aria-label='Add stock']");
            await Assertions.Expect(addStockSheet).ToBeVisibleAsync();

            await addStockSheet.Locator("[name='AddStockInput.Quantity']").FillAsync("500");
            await addStockSheet.Locator("[name='AddStockInput.UnitId']")
                .SelectOptionAsync(new SelectOptionValue { Label = "g — gram" });
            await addStockSheet.Locator("[name='AddStockInput.LocationId']")
                .SelectOptionAsync(new SelectOptionValue { Label = "Pantry" });
            await addStockSheet.GetByRole(AriaRole.Button, new() { Name = "Add to pantry" }).ClickAsync();

            await Assertions.Expect(page.Locator(".page-header__subtitle", new() { HasText = "500 g in stock" }))
                .ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Consume" })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#lots-grid")).ToContainTextAsync("500 g");

            // ── Step 7: Archive it (back on the catalog definition page) ──────────
            await page.GotoAsync($"{BaseUrl}/Catalog/Products/{productId}");
            await page.ClickAsync("button:has-text('Archive')");
            await page.WaitForURLAsync("**/Catalog/Products/**");
            await Assertions.Expect(page.Locator(".page-header__title-meta", new() { HasText = "archived" })).ToBeVisibleAsync();

            // ── Step 8: Archiving a stocked product must never hide its on-hand stock
            // (plantry-lxm2, gap 2) — the row stays visible in the default "In stock" scope with
            // its real quantity and a neutral "Archived" badge, and the name link routes through
            // to /Catalog/Products/{id} (the Unarchive control lives there, not on the Pantry
            // stock detail page) rather than disappearing — fixing gap 1's dead end too. ────────
            await page.GotoAsync($"{BaseUrl}/Catalog/Products");
            await page.WaitForURLAsync("**/Pantry**");
            var archivedRow = page.Locator("tr", new() { HasText = renamedProductName });
            await Assertions.Expect(archivedRow).ToBeVisibleAsync();
            await Assertions.Expect(archivedRow).ToContainTextAsync("500 g");
            await Assertions.Expect(archivedRow.Locator(".badge", new() { HasText = "Archived" })).ToBeVisibleAsync();

            await archivedRow.Locator("a.data-grid__link").ClickAsync();
            await page.WaitForURLAsync($"**/Catalog/Products/{productId}");
            await Assertions.Expect(page.Locator(".page-header__title-meta", new() { HasText = "archived" })).ToBeVisibleAsync();
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
