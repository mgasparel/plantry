using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Npgsql;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E — the J5 "Add missing to shopping list" journey (P2-4b done-when):
///
///   Register household → seed a tracked product (NO stock) → create a recipe using that
///   product → open the Detail page → assert the button reads "Add 1 missing to shopping
///   list" → tap it → assert the button flips to "Added" → navigate to Shopping → assert
///   the missing ingredient appears on the list (source=recipe provenance).
///
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class RecipeAddMissingJourneyTests(AppHostFixture appHost) : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    private string BaseUrl => appHost.BaseUrl;

    /// <summary>Matches /Recipes/{guid} — Detail page only (not /Edit, /Cook, /New).</summary>
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

    // ── Journey: Detail → Add missing → Shopping list populated ───────────────

    [Fact(DisplayName = "J5 Add missing: tap Add missing button on Detail → button flips to Added → ingredient appears on Shopping list")]
    public async Task AddMissing_TapsButton_FlipsToAddedAndPopulatesShoppingList()
    {
        var email = $"add-missing-{Guid.NewGuid():N}@test.local";
        // Truncate to 24 chars to avoid DB length issues.
        var productName = $"Missing Butter {Guid.NewGuid():N}"[..24];
        var recipeName  = $"Butter Cake {Guid.NewGuid():N}"[..20];

        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── Step 1: Register a fresh household ───────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", "Add-Missing Journey Household");
            await page.FillAsync("[name='Input.Email']", email);
            await page.FillAsync("[name='Input.DisplayName']", "Missing User");
            await page.FillAsync("[name='Input.Password']", "testpass1");
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── Step 2: Seed a tracked catalog product (no stock added — it will be Missing) ─
            await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
            await page.WaitForURLAsync("**/Catalog/Products/Create");
            await page.FillAsync("[name='Input.Name']", productName);
            await page.SelectOptionAsync("[name='Input.DefaultUnitId']", new SelectOptionValue { Label = "g — gram" });
            await page.ClickAsync("button[type=submit]:has-text('Add product')");
            await page.WaitForURLAsync("**/Catalog/**");

            // ── Step 3: Create a recipe using this (missing) product ─────────────
            await page.GotoAsync($"{BaseUrl}/Recipes/New");
            await page.WaitForURLAsync("**/Recipes/New");
            await page.FillAsync("[name='Input.Name']", recipeName);
            await page.FillAsync("[name='Input.DefaultServings']", "2");

            // Add the missing product as an ingredient via the ingredient sheet.
            // Scope to #recipe-editor to avoid strict-mode collision with the global
            // quick-add sheet in _Layout.
            await page.ClickAsync("button:has-text('Add ingredient')");
            var ingSheet = page.Locator("#recipe-editor .sheet");
            await Assertions.Expect(ingSheet).ToBeVisibleAsync();
            await ingSheet.Locator("input[role='combobox']").PressSequentiallyAsync(productName[..8]);
            var ingOption = ingSheet.Locator(".searchable-select__listbox li[role='option']", new() { HasText = productName });
            await Assertions.Expect(ingOption).ToBeVisibleAsync();
            await ingOption.ClickAsync();
            // `:visible` targets the search-view Quantity; the create-view Quantity (plantry-guab) is
            // x-show hidden here, mirroring the `select:visible` disambiguation on the next line.
            await ingSheet.Locator("input[type='number']:visible").FillAsync("100");
            await ingSheet.Locator("select:visible").SelectOptionAsync(new SelectOptionValue { Label = "g" });
            // Use .First to target the search-view "Add" button (two .sheet__actions bars exist after
            // the plantry-nb4x two-view scaffold; .First avoids the strict-mode violation).
            await ingSheet.Locator(".sheet__actions button.btn--primary").First.ClickAsync();
            await Assertions.Expect(ingSheet).Not.ToBeVisibleAsync();

            await page.ClickAsync("button[type=submit]:has-text('Create recipe')");
            await page.WaitForURLAsync(DetailUrlPattern);

            var detailUrl = page.Url;

            // ── Step 4: Assert the "Add missing" button is visible and labelled correctly ─
            // The product has no stock, so fulfillment shows 1 missing ingredient.
            var addMissingBtn = page.Locator("button.btn--soft", new() { HasText = "missing to shopping list" });
            await Assertions.Expect(addMissingBtn).ToBeVisibleAsync();
            // Button should contain the count "1" (1 missing ingredient).
            await Assertions.Expect(addMissingBtn).ToContainTextAsync("1");
            // Button should NOT be disabled.
            await Assertions.Expect(addMissingBtn).ToBeEnabledAsync();

            // ── Step 5: Tap the button and wait for the htmx POST to round-trip ───
            // The button keeps BOTH the "Add … missing" and "Added" spans in the DOM permanently
            // (Alpine x-show/x-cloak only toggle display:none), so a textContent assertion such as
            // ToContainText("Added") matches the hidden span INSTANTLY and does not wait for the
            // POST. Gate on the POST response and the user-visible disabled state instead — the
            // button is disabled (:disabled="missingAdded") only after htmx:after-request fires on a
            // successful 2xx, which is sent only after the server has committed the write. This
            // guarantees the row is persisted before we navigate to /Shopping.
            await page.RunAndWaitForResponseAsync(
                async () => await addMissingBtn.ClickAsync(),
                r => r.Url.Contains("handler=AddMissing") && r.Status == 200);
            await Assertions.Expect(addMissingBtn).ToBeDisabledAsync();

            // ── Step 6: Navigate to Shopping and assert the ingredient appears ─────
            await page.GotoAsync($"{BaseUrl}/Shopping");
            await page.WaitForURLAsync("**/Shopping**");

            // The missing product should now appear on the shopping list.
            await Assertions.Expect(page.Locator("#shopping-list")).ToContainTextAsync(productName);

            // ── Step 7: Assert source=recipe provenance in the DB ────────────────
            var productId = await GetProductIdAsync(productName);
            Assert.True(productId.HasValue, $"Product '{productName}' not found in catalog.");
            var sourceOk = await ShoppingItemHasRecipeSourceAsync(productId!.Value);
            Assert.True(sourceOk,
                $"Expected shopping list item for product '{productName}' to have source='recipe', but it was not found.");
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-add-missing.zip" });
        }
    }

    // ── DB helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid?> GetProductIdAsync(string productName)
    {
        await using var conn = new NpgsqlConnection(appHost.DbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id FROM catalog.products WHERE name = @n LIMIT 1", conn);
        cmd.Parameters.AddWithValue("@n", productName);
        return await cmd.ExecuteScalarAsync() as Guid?;
    }

    /// <summary>
    /// Returns true if a shopping list item for the given product exists with source='recipe'.
    /// Reads as the database owner, not subject to RLS.
    /// </summary>
    private async Task<bool> ShoppingItemHasRecipeSourceAsync(Guid productId)
    {
        await using var conn = new NpgsqlConnection(appHost.DbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"SELECT COUNT(*) FROM shopping.shopping_list_item i
              JOIN shopping.shopping_list_item_contribution c
                ON c.shopping_list_item_id = i.shopping_list_item_id
              WHERE i.product_id = @p AND c.source = 'recipe'",
            conn);
        cmd.Parameters.AddWithValue("@p", productId);
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        return count > 0;
    }
}
