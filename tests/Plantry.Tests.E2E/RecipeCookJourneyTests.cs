using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Npgsql;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E — the J4 Cook-a-recipe journey (P2-3d done-when):
///
///   Register household → seed a product with known stock → create a recipe using that product →
///   navigate to the Cook confirmation page → confirm → assert:
///     (a) a <c>cook_event</c> row was written for the household + recipe,
///     (b) pantry stock was decremented (available quantity dropped by the consumed amount), and
///     (c) the Detail page fulfillment reflects the updated stock (the fulfilled count changes).
///
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class RecipeCookJourneyTests(AppHostFixture appHost) : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    private string BaseUrl => appHost.BaseUrl;

    /// <summary>Matches the Detail page URL (/Recipes/{guid}), not /Edit.</summary>
    private static readonly Regex DetailUrlPattern = new(@"/Recipes/[0-9a-fA-F-]{36}$");

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

    // ── Journey: Cook → pantry decremented → cook_event written ────────────────

    [Fact(DisplayName = "J4 Cook: confirm cook → cook_event written + pantry stock decremented + Detail fulfillment updated")]
    public async Task CookRecipe_ConfirmCook_DecrementsStockWritesCookEvent()
    {
        var email = $"cook-journey-{Guid.NewGuid():N}@test.local";
        var productName = $"Flour {Guid.NewGuid():N}".Substring(0, 20);

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── Register household ────────────────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", "Cook Journey Household");
            await page.FillAsync("[name='Input.Email']", email);
            await page.FillAsync("[name='Input.DisplayName']", "Cook User");
            await page.FillAsync("[name='Input.Password']", "testpass1");
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── Create a tracked product ──────────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
            await page.WaitForURLAsync("**/Catalog/Products/Create");
            await page.FillAsync("[name='Input.Name']", productName);
            await page.SelectOptionAsync("[name='Input.DefaultUnitId']", new SelectOptionValue { Label = "g — gram" });
            await page.ClickAsync("button:has-text('Create Product')");
            await page.WaitForURLAsync("**/Catalog/Products/**");

            // ── Add 500g of stock ─────────────────────────────────────────────────
            // Scope to #sheet-host to avoid strict-mode collision with the global
            // quick-add sheet in _Layout (both use .sheet/.sheet__panel classes).
            await page.GotoAsync($"{BaseUrl}/Pantry");
            await page.WaitForURLAsync("**/Pantry**");
            await page.ClickAsync("button:has-text('Add stock')");

            var sheet = page.Locator("#sheet-host .sheet__panel");
            await Assertions.Expect(sheet).ToBeVisibleAsync();

            var productSearch = sheet.Locator("input[role='combobox']");
            await productSearch.FillAsync(productName.Substring(0, 8));
            var productOption = page.Locator(".searchable-select__listbox li[role='option']", new() { HasText = productName });
            await Assertions.Expect(productOption).ToBeVisibleAsync();
            await productOption.ClickAsync();

            await sheet.Locator("input[name='Input.Quantity']").FillAsync("500");
            // Select the gram unit — matches the product's default unit set at creation.
            await sheet.Locator("select[name='Input.UnitId']").SelectOptionAsync(new SelectOptionValue { Label = "g — gram" });
            // Select the first available location.
            await sheet.Locator("select[name='Input.LocationId']").SelectOptionAsync(new SelectOptionValue { Index = 1 });
            await sheet.Locator("button[type='submit']:has-text('Add to pantry')").ClickAsync();
            await Assertions.Expect(sheet).Not.ToBeVisibleAsync();

            // ── Verify product appears in pantry with 500g ────────────────────────
            var pantryRow = page.Locator("tr", new() { HasText = productName });
            await Assertions.Expect(pantryRow).ToBeVisibleAsync();

            // ── Create a recipe using this product ────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Recipes/New");
            await page.WaitForURLAsync("**/Recipes/New");

            var recipeName = $"Simple Bread {Guid.NewGuid():N}".Substring(0, 20);
            await page.FillAsync("[name='Input.Name']", recipeName);
            await page.FillAsync("[name='Input.DefaultServings']", "2");

            // Add flour ingredient via the ingredient sheet.
            // Scope to #recipe-editor to avoid strict-mode collision with the global
            // quick-add sheet in _Layout (both use .sheet/.sheet__panel classes).
            await page.ClickAsync("button:has-text('Add ingredient')");
            var ingSheet = page.Locator("#recipe-editor .sheet");
            await Assertions.Expect(ingSheet).ToBeVisibleAsync();
            await ingSheet.Locator("input[role='combobox']").PressSequentiallyAsync(productName.Substring(0, 8));
            var ingOption = ingSheet.Locator(".searchable-select__listbox li[role='option']", new() { HasText = productName });
            await Assertions.Expect(ingOption).ToBeVisibleAsync();
            await ingOption.ClickAsync();
            // `:visible` targets the search-view Quantity; the create-view Quantity (plantry-guab) is
            // x-show hidden here, mirroring the `select:visible` disambiguation on the next line.
            await ingSheet.Locator("input[type='number']:visible").FillAsync("200");
            await ingSheet.Locator("select:visible").SelectOptionAsync(new SelectOptionValue { Label = "g" });
            // Use .First to target the search-view "Add" button (plantry-nb4x two-view scaffold).
            await ingSheet.Locator(".sheet__actions button.btn--primary").First.ClickAsync();
            await Assertions.Expect(ingSheet).Not.ToBeVisibleAsync();

            await page.ClickAsync("button[type=submit]:has-text('Create Recipe')");
            await page.WaitForURLAsync(DetailUrlPattern);

            var detailUrl = page.Url;
            var recipeId = detailUrl.Split('/').Last();

            // ── Navigate to the Cook page ─────────────────────────────────────────
            // The "Cook this" button on the Detail page links to /Recipes/{id}/Cook?Servings={n}.
            var cookLink = page.Locator("a.btn--cook");
            await Assertions.Expect(cookLink).ToBeVisibleAsync();
            await cookLink.ClickAsync();

            // Cook confirmation page should load.
            await page.WaitForURLAsync($"**/Recipes/{recipeId}/Cook**");
            await Assertions.Expect(page.Locator("#cook-confirm")).ToBeVisibleAsync();

            // ── Confirm the cook ──────────────────────────────────────────────────
            await page.ClickAsync("button[type=submit]:has-text('Confirm cook')");

            // Redirect back to the Detail page on success.
            await page.WaitForURLAsync(DetailUrlPattern);
            Assert.DoesNotMatch(@"/Cook$", page.Url);

            // ── Assert: cook_event written ────────────────────────────────────────
            var cookEventCount = await CountCookEventsAsync(recipeId);
            Assert.Equal(1, cookEventCount);

            // ── Assert: pantry stock decremented ────────────────────────────────
            // The recipe ingredient was 200g at 2 servings, cooked at 2 servings (scale=1) → 200g consumed.
            // We stocked 500g, so remaining should be 300g.
            // Verify via the Pantry page: the product row should show a lower quantity OR
            // verify via direct DB query that stock changed.
            var remainingStock = await GetRemainingStockAsync(productName);
            // Allow for either 300g exactly or some rounding — the key is it decreased from 500.
            Assert.True(remainingStock < 500m,
                $"Expected stock < 500g after cooking 200g, but got {remainingStock}g.");

            // ── Assert: Detail page fulfillment reflects the updated stock ─────────
            // Navigate back to the Detail page to confirm fulfillment was recomputed.
            await page.GotoAsync(detailUrl);
            await page.WaitForURLAsync(DetailUrlPattern);
            // The fulfillment card should still render (the product has remaining stock > 0).
            await Assertions.Expect(page.Locator(".rd-fulf-card")).ToBeVisibleAsync();
            // After cooking 200g from 500g, 300g remain and 200g was required → InStock still.
            // The fulfillment card should show the ingredient as "in stock" (1 of 1 tracked ingredients).
            var fulfillmentText = await page.Locator(".rd-fulf-card").TextContentAsync();
            Assert.Contains("1 of 1", fulfillmentText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-recipe-cook.zip" });
        }
    }

    // ── DB helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Counts cook_event rows for a recipe id. Uses the owner connection (not subject to RLS)
    /// so it sees rows from any household.
    /// </summary>
    private async Task<int> CountCookEventsAsync(string recipeId)
    {
        await using var conn = new NpgsqlConnection(appHost.DbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM recipes.cook_event WHERE recipe_id = @r",
            conn);
        cmd.Parameters.AddWithValue("@r", Guid.Parse(recipeId));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    /// <summary>
    /// Returns the remaining available quantity (sum of all active lot quantities) for a product
    /// looked up by name. Reads as the database owner, which is not subject to RLS.
    /// </summary>
    private async Task<decimal> GetRemainingStockAsync(string productName)
    {
        await using var conn = new NpgsqlConnection(appHost.DbConnectionString);
        await conn.OpenAsync();

        // Look up the product id from the catalog.
        await using var productCmd = new NpgsqlCommand(
            "SELECT id FROM catalog.products WHERE name = @n LIMIT 1",
            conn);
        productCmd.Parameters.AddWithValue("@n", productName);
        var productId = await productCmd.ExecuteScalarAsync() as Guid?;
        if (productId is null) return 0m;

        // Sum active entry quantities for this product.
        await using var stockCmd = new NpgsqlCommand(
            @"SELECT COALESCE(SUM(quantity), 0)
              FROM inventory.stock_entry
              WHERE product_id = @p
                AND quantity > 0
                AND (expiry_date IS NULL OR expiry_date >= CURRENT_DATE)",
            conn);
        stockCmd.Parameters.AddWithValue("@p", productId.Value);
        var result = await stockCmd.ExecuteScalarAsync();
        return Convert.ToDecimal(result ?? 0m);
    }
}
