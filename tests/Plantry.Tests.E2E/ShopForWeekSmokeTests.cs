using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Npgsql;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E smoke tests for the "Shop for this week" feature (P3-4, J6).
///
/// Journey: register → create product (no stock = Missing) → create recipe using that
/// product → assign recipe to current week's meal plan → click "Shop for this week"
/// → assert "N items added" completion label → navigate to /Shopping → assert product
/// appears on list → re-shop (reload + re-click) → assert exactly ONE row on Shopping
/// (merge, not duplicate) → DB check: source=meal_plan.
///
/// This satisfies L5: "plan a week → shop → items appear on the list, and re-shop
/// MERGES (no duplicates)."
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class ShopForWeekSmokeTests(AppHostFixture appHost) : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    private string BaseUrl => appHost.BaseUrl;

    /// <summary>Matches /Recipes/{guid} — Detail page only.</summary>
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

    [Fact(DisplayName = "Shop-for-week: plan recipe → shop → item on list (source=meal_plan) → re-shop MERGES (no duplicate row)")]
    public async Task ShopForWeek_PlanRecipe_ItemAppearsOnList_ReShopMerges()
    {
        var uniqueEmail = $"smoke-shop-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";
        var productName = $"Shop Butter {Guid.NewGuid():N}"[..24];
        var recipeName = $"Butter Cake {Guid.NewGuid():N}"[..20];

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── Step 1: Register a fresh household ────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", "Shop Week Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "Shop User");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── Step 2: Seed a tracked catalog product (no stock → will be Missing) ─
            await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
            await page.WaitForURLAsync("**/Catalog/Products/Create");
            await page.FillAsync("[name='Input.Name']", productName);
            await page.SelectOptionAsync("[name='Input.DefaultUnitId']", new SelectOptionValue { Label = "g — gram" });
            await page.ClickAsync("button[type=submit]:has-text('Add product')");
            await page.WaitForURLAsync("**/Catalog/**");

            // ── Step 3: Create a recipe using this product (100g required) ────────
            await page.GotoAsync($"{BaseUrl}/Recipes/New");
            await page.WaitForURLAsync("**/Recipes/New");
            await page.FillAsync("[name='Input.Name']", recipeName);
            await page.FillAsync("[name='Input.DefaultServings']", "2");

            // Add the missing product as an ingredient via the ingredient sheet.
            // Scope to #recipe-editor to avoid strict-mode collision with global quick-add.
            await page.ClickAsync("button:has-text('Add ingredient')");
            var ingSheet = page.Locator("#recipe-editor .sheet");
            await Assertions.Expect(ingSheet).ToBeVisibleAsync();
            await ingSheet.Locator("input[role='combobox']").PressSequentiallyAsync(productName[..8]);
            var ingOption = page.Locator("#prod-list-sheet li[role='option']", new() { HasText = productName });
            await Assertions.Expect(ingOption).ToBeVisibleAsync();
            await ingOption.ClickAsync();
            await ingSheet.Locator("input[type='number']").FillAsync("100");
            await ingSheet.Locator("select:visible").SelectOptionAsync(new SelectOptionValue { Label = "g" });
            await ingSheet.Locator(".sheet__actions button.btn--primary").ClickAsync();
            await Assertions.Expect(ingSheet).Not.ToBeVisibleAsync();

            await page.ClickAsync("button[type=submit]:has-text('Create recipe')");
            await page.WaitForURLAsync(DetailUrlPattern);

            // Extract the recipe ID from the URL (e.g. /Recipes/xxxxxxxx-xxxx-...).
            var recipeId = new Uri(page.Url).Segments.Last().TrimEnd('/');
            Assert.True(Guid.TryParse(recipeId, out _), $"Expected recipe GUID in URL, got: {page.Url}");

            // ── Step 4: Navigate to Meal Plan and assign the recipe to this week ──
            await page.GetByRole(AriaRole.Link, new() { Name = "Meal Plan" }).First.ClickAsync();
            await page.WaitForURLAsync("**/MealPlan**");
            await Assertions.Expect(page.Locator(".wkgrid")).ToBeVisibleAsync();

            // Extract the first empty cell's date + slotId from its hx-get attribute.
            var hxGet = await page.Locator(".empty-add").First.GetAttributeAsync("hx-get");
            Assert.NotNull(hxGet);
            var qs = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + hxGet).Query);
            var cellDate = qs["date"] ?? "";
            var slotId = qs["slotId"] ?? "";
            Assert.NotEmpty(cellDate);
            Assert.NotEmpty(slotId);

            // POST Assign recipe dish via fetch (same path as the Alpine editor Save button).
            var token = await GetAntiforgeryTokenAsync(page);
            var assignUrl = $"{BaseUrl}/MealPlan?handler=Assign&date={cellDate}&slotId={slotId}";
            var assignStatus = await page.EvaluateAsync<int>(@"
                async (args) => {
                    const params = new URLSearchParams();
                    params.append('mode', 'dishes');
                    params.append('dishKinds', 'recipe');
                    params.append('dishItemIds', args.recipeId);
                    params.append('dishServings', '2');
                    const r = await fetch(args.url, {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/x-www-form-urlencoded',
                            'RequestVerificationToken': args.token,
                            'HX-Request': 'true'
                        },
                        body: params.toString()
                    });
                    return r.status;
                }", new { url = assignUrl, recipeId, token });
            Assert.Equal(200, assignStatus);

            // ── Step 5: Reload Meal Plan and assert the "Shop for this week" button ─
            await page.GotoAsync($"{BaseUrl}/MealPlan");
            await page.WaitForURLAsync("**/MealPlan**");
            await Assertions.Expect(page.Locator(".wkgrid")).ToBeVisibleAsync();

            // Budget chip is present and shows "/ wk"
            await Assertions.Expect(page.Locator(".budget-chip")).ToBeVisibleAsync();
            var chipText = await page.Locator(".budget-chip").TextContentAsync();
            Assert.Contains("/ wk", chipText);

            // The "Shop for this week" button is visible and enabled
            var shopBtn = page.Locator("button:has-text('Shop for this week')");
            await Assertions.Expect(shopBtn).ToBeVisibleAsync();
            await Assertions.Expect(shopBtn).ToBeEnabledAsync();

            // ── Step 6: Click the button and assert "N items added" ───────────────
            // The Alpine shopWeek() component does a JSON fetch; gate on the POST
            // response before asserting completion state (mirrors j5-recipeaddmissingjourney memory).
            await page.RunAndWaitForResponseAsync(
                () => shopBtn.ClickAsync(),
                resp => resp.Url.Contains("MealPlan") && resp.Url.Contains("handler=Shop") && resp.Status == 200);

            // Button is now disabled (done=true) with a completion label
            await Assertions.Expect(shopBtn).ToBeDisabledAsync();
            var btnText = await shopBtn.TextContentAsync();
            // Recipe has 1 Missing ingredient → "1 item added"
            Assert.True(
                btnText!.Contains("item added") || btnText.Contains("items added"),
                $"Expected 'item(s) added' completion text, got: '{btnText}'");

            // ── Step 7: Navigate to Shopping and assert the product appears ────────
            await page.GotoAsync($"{BaseUrl}/Shopping");
            await page.WaitForURLAsync("**/Shopping**");
            await Assertions.Expect(page.Locator("#shopping-list")).ToContainTextAsync(productName);

            // ── Step 8: Re-shop — reload MealPlan, click again ────────────────────
            // After page reload the Alpine component resets (done=false, button enabled).
            await page.GotoAsync($"{BaseUrl}/MealPlan");
            await page.WaitForURLAsync("**/MealPlan**");
            await Assertions.Expect(page.Locator(".wkgrid")).ToBeVisibleAsync();

            var shopBtn2 = page.Locator("button:has-text('Shop for this week')");
            await Assertions.Expect(shopBtn2).ToBeEnabledAsync();

            await page.RunAndWaitForResponseAsync(
                () => shopBtn2.ClickAsync(),
                resp => resp.Url.Contains("MealPlan") && resp.Url.Contains("handler=Shop") && resp.Status == 200);

            await Assertions.Expect(shopBtn2).ToBeDisabledAsync();

            // ── Step 9: Navigate back to Shopping — product still appears ONCE ─────
            await page.GotoAsync($"{BaseUrl}/Shopping");
            await page.WaitForURLAsync("**/Shopping**");
            await Assertions.Expect(page.Locator("#shopping-list")).ToContainTextAsync(productName);

            // Exactly one row for this product (merged, not duplicated)
            var itemCount = await page.Locator(".sl-item", new() { HasText = productName }).CountAsync();
            Assert.Equal(1, itemCount);

            // ── Step 10: DB check — source = meal_plan ────────────────────────────
            var productId = await GetProductIdAsync(productName);
            Assert.True(productId.HasValue, $"Product '{productName}' not found in catalog.");
            var sourceMealPlan = await ShoppingItemHasMealPlanSourceAsync(productId!.Value);
            Assert.True(sourceMealPlan,
                $"Expected shopping list item for '{productName}' to have source='meal_plan'.");
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-shop-for-week.zip" });
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static async Task<string> GetAntiforgeryTokenAsync(IPage page)
    {
        return await page.Locator("input[name=__RequestVerificationToken]").First
                   .GetAttributeAsync("value") ?? "";
    }

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
    /// Returns true if a shopping list item for the given product exists with source='meal_plan'.
    /// Reads as the database owner, not subject to RLS.
    /// </summary>
    private async Task<bool> ShoppingItemHasMealPlanSourceAsync(Guid productId)
    {
        await using var conn = new NpgsqlConnection(appHost.DbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"SELECT COUNT(*) FROM shopping.shopping_list_item
              WHERE product_id = @p AND source = 'meal_plan'",
            conn);
        cmd.Parameters.AddWithValue("@p", productId);
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        return count > 0;
    }
}
