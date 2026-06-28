using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E smoke test for the cook-now recipe picks band on the Today (Home) page
/// (SPEC Page 0 §0c, plantry-81g acceptance criteria):
///
///   "A cookable recipe appears as a pick and Cook opens its cook flow."
///
/// Journey:
///   Register a fresh household → create a product → add stock → create a recipe using that product
///   → navigate to Today → verify the cook-now picks band shows the recipe → click Cook
///   → verify the Cook page opens for that recipe.
///
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class TodayCookNowSmokeTests(AppHostFixture appHost) : IAsyncLifetime
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

    [Fact(DisplayName = "Today: cookable recipe appears as a cook-now pick and Cook opens the cook flow (plantry-81g/AC)")]
    public async Task Today_CookNow_CookableRecipeAppears_AndCookOpensFlow()
    {
        var uniqueEmail = $"cooknow-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";
        var productName = $"CN {Guid.NewGuid():N}".Substring(0, 15);
        var recipeName  = $"CR {Guid.NewGuid():N}".Substring(0, 15);

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
            await page.FillAsync("[name='Input.HouseholdName']", "CookNow Test Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "CookNow Tester");
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

            // ── 3. Add stock for that product ─────────────────────────────────────
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
            await Assertions.Expect(pantryRow).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

            // ── 4. Create a recipe using that product ─────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Recipes/New");
            await page.WaitForURLAsync("**/Recipes/New");
            await page.FillAsync("[name='Input.Name']", recipeName);

            // Add ingredient via the ingredient sheet (same pattern as RecipeCookJourneyTests).
            await page.ClickAsync("button:has-text('Add ingredient')");
            var ingSheet = page.Locator("#recipe-editor .sheet");
            await Assertions.Expect(ingSheet).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 15000 });
            await ingSheet.Locator("input[role='combobox']").PressSequentiallyAsync(productName[..8]);
            var ingOption = page.Locator("#prod-list-sheet li[role='option']", new() { HasText = productName });
            await Assertions.Expect(ingOption).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 15000 });
            await ingOption.ClickAsync();
            await ingSheet.Locator("input[type='number']").FillAsync("100");
            await ingSheet.Locator("select:visible").SelectOptionAsync(new SelectOptionValue { Label = "g" });
            await ingSheet.Locator(".sheet__actions button.btn--primary").ClickAsync();
            await Assertions.Expect(ingSheet).Not.ToBeVisibleAsync();

            await page.ClickAsync("button[type=submit]:has-text('Create recipe')");
            await page.WaitForURLAsync("**/Recipes/**");

            // ── 5. Navigate to Today and verify cook-now picks band ───────────────
            await page.GotoAsync($"{BaseUrl}/Today");
            await page.WaitForURLAsync("**/Today**");
            await page.Locator(".today-wrap").WaitForAsync();

            // The cook-now picks band must be visible with a section header
            var mealsBand = page.Locator("#today-meals-band");
            await Assertions.Expect(mealsBand).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

            // The recipe must appear as a pick card
            var pickCard = page.Locator(".today-pick", new() { HasText = recipeName });
            await Assertions.Expect(pickCard).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 15000 });

            // "Ready to cook" hint should be visible on the pick (all ingredients in stock)
            var readyHint = pickCard.Locator(".today-pick__hint--ready");
            await Assertions.Expect(readyHint).ToBeVisibleAsync();

            // ── 6. Click Cook and verify the Cook page opens ──────────────────────
            var cookBtn = pickCard.Locator(".today-pick__cook");
            await Assertions.Expect(cookBtn).ToBeVisibleAsync();
            await cookBtn.ClickAsync();

            // The Cook page URL should contain the recipe id and /Cook
            await page.WaitForURLAsync("**/Recipes/**/Cook**");
            var cookTitle = page.Locator(".cook-title");
            await Assertions.Expect(cookTitle).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 15000 });
            await Assertions.Expect(cookTitle).ToContainTextAsync(
                "Cook", new LocatorAssertionsToContainTextOptions { IgnoreCase = true });
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-today-cook-now.zip" });
        }
    }
}
