using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Npgsql;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 regression journey for product-owned unit conversions in the Meal Plan editor. A product
/// whose default unit is grams and whose catalog conversion is servings ↔ grams must expose servings
/// in the editor picker, and the selected 2 srv dish must survive the AssignJson save path.
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class MealPlanProductConversionJourneyTests(AppHostFixture appHost) : IAsyncLifetime
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

    [Fact(DisplayName = "Meal Plan editor: product conversion exposes srv and saves a 2 srv product dish")]
    public async Task ProductConversion_UnitOption_IsSelectable_AndDishPersists()
    {
        var uniqueEmail = $"e2e-meal-conv-{Guid.NewGuid():N}@test.local";
        var productName = $"Pasta Dry Spaghetti {Guid.NewGuid():N}"[..38];
        const string password = "testpass1";

        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            await RegisterHouseholdAsync(page, uniqueEmail, password);
            await SeedProductWithConversionAsync(page, productName);
            var product = await GetProductDetailsAsync(productName);
            Assert.True(product.HasValue, $"Product '{productName}' was not found after creation.");

            await page.GotoAsync($"{BaseUrl}/MealPlan");
            await page.WaitForURLAsync("**/MealPlan**");
            await Assertions.Expect(page.Locator(".wkgrid")).ToBeVisibleAsync();

            // Open the first empty cell in the same way the rendered grid does. Capture the
            // cell's date/slotId now — needed later (plantry-qybt) to merge a recipe dish into
            // this same slot via a direct AssignJson call once it is no longer ".empty-add".
            var emptyAdd = page.Locator(".empty-add").First;
            var emptyAddOnclick = await emptyAdd.GetAttributeAsync("onclick");
            Assert.NotNull(emptyAddOnclick);
            var cellMatch = Regex.Match(emptyAddOnclick!, @"openEditor\('([^']+)',\s*'([^']+)',\s*null\)");
            Assert.True(cellMatch.Success, $"Could not parse openEditor from onclick: {emptyAddOnclick}");
            var cellDate = cellMatch.Groups[1].Value;
            var cellSlotId = cellMatch.Groups[2].Value;

            await emptyAdd.ClickAsync();
            var dialog = page.Locator("#meal-editor-dialog");
            await Assertions.Expect(dialog).ToBeVisibleAsync();

            // Search through the real editor island and add the product hit.
            var search = dialog.Locator(".dish-search input");
            await search.PressSequentiallyAsync(productName);
            var productHit = dialog.Locator(".dish-menu .dish-opt", new() { HasText = productName });
            await Assertions.Expect(productHit).ToBeVisibleAsync();
            await productHit.ClickAsync();

            var quantityInput = dialog.Locator(".mp-dish-qty input.stepper__val");
            var unitSelect = dialog.Locator("select.meal-unit-picker__select");
            await Assertions.Expect(quantityInput).ToBeVisibleAsync();
            await Assertions.Expect(unitSelect).ToBeVisibleAsync();
            Assert.Contains("stepper__val", await quantityInput.GetAttributeAsync("class") ?? string.Empty);
            Assert.Contains("meal-unit-picker__select", await unitSelect.GetAttributeAsync("class") ?? string.Empty);

            // The quantity control is the canonical compact stepper contract shared
            // with recipe rows (plantry-qybt); the unit picker is its neighbouring
            // borderless native select within the same bordered composite.  Keep a
            // light layout guard without coupling the journey to incidental button
            // widths.
            var stepper = dialog.Locator(".mp-dish-qty > .stepper");
            await Assertions.Expect(stepper).ToBeVisibleAsync();
            var stepperBox = await stepper.BoundingBoxAsync();
            var unitBox = await unitSelect.BoundingBoxAsync();
            Assert.NotNull(stepperBox);
            Assert.NotNull(unitBox);
            Assert.InRange(Math.Abs(stepperBox!.Y - unitBox!.Y), 0, 2);

            var unitLabels = await unitSelect.Locator("option").AllTextContentsAsync();
            Assert.Contains("srv", unitLabels);

            await quantityInput.FillAsync("2");
            await unitSelect.SelectOptionAsync(new SelectOptionValue { Label = "srv" });

            await page.RunAndWaitForResponseAsync(
                () => dialog.Locator("button.btn--primary", new() { HasText = "Save meal" }).ClickAsync(),
                response => response.Url.Contains("handler=AssignJson") && response.Status == 200);
            await Assertions.Expect(dialog).ToBeHiddenAsync();

            var saved = await GetSavedProductDishAsync(product!.Value.ProductId);
            Assert.True(saved.HasValue, "The product dish was not persisted.");
            Assert.Equal(2m, saved.Value.Quantity);
            Assert.Equal(product.Value.ServingUnitId, saved.Value.UnitId);

            // A fresh server render must still show the selected unit, proving this was not only
            // an island-local draft value.
            await page.ReloadAsync();
            var savedCard = page.Locator(".meal-card", new() { HasText = productName });
            await Assertions.Expect(savedCard).ToBeVisibleAsync();
            await Assertions.Expect(savedCard).ToContainTextAsync("2 srv");

            // Reopen the saved product through its own card. The dedicated edit pencil was removed
            // (plantry-a6me) — the card's click-anywhere handler (plantry-ely3) is now the mouse path,
            // so click a non-interactive area of the card (.mc-photo) rather than a nested button/link.
            await savedCard.Locator(".mc-photo").ClickAsync();
            await Assertions.Expect(dialog).ToBeVisibleAsync();
            await Assertions.Expect(dialog.Locator(".mp-dish-qty input.stepper__val"))
                .ToHaveValueAsync("2");
            await Assertions.Expect(dialog.Locator("select.meal-unit-picker__select"))
                .ToHaveValueAsync(product!.Value.ServingUnitId.ToString());
            await dialog.Locator("button.btn--secondary", new() { HasText = "Cancel" }).ClickAsync();
            await Assertions.Expect(dialog).ToBeHiddenAsync();

            // ── plantry-qybt: recipe row / product row parity ───────────────────────────
            // Merge a recipe dish into the same slot via a direct AssignJson call — recipe
            // dishes are not existence-validated on assign, only rendered via the server's
            // name-resolution fallback ("Unknown recipe"), the same pattern proven in
            // MealCardClickAnywhereJourneyTests. Pass the real mealId so this updates the
            // slot's existing meal rather than creating a second one at the same date/slot.
            var mealId = await GetMealIdForProductDishAsync(product!.Value.ProductId);
            Assert.True(mealId.HasValue, "Could not resolve the planned meal id for the saved product dish.");

            var token = await page.Locator("input[name=__RequestVerificationToken]").First.GetAttributeAsync("value") ?? "";
            var assignUrl = $"{BaseUrl}/MealPlan?handler=AssignJson";
            var mixedAssignStatus = await page.EvaluateAsync<int>(@"
                async (args) => {
                    const body = JSON.stringify({
                        mode: 'dishes',
                        dishes: [
                            { kind: 'recipe', itemId: '00000000-0000-0000-0000-000000000099', servings: 2 },
                            { kind: 'product', itemId: args.productId, quantity: 2, unitId: args.unitId }
                        ],
                        att: null,
                        attendeesOverridden: false,
                        mealId: args.mealId,
                        date: args.date,
                        slotId: args.slotId
                    });
                    const r = await fetch(args.url, {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json',
                            'RequestVerificationToken': args.token,
                            'X-Requested-With': 'XMLHttpRequest'
                        },
                        body
                    });
                    return r.status;
                }",
                new
                {
                    url = assignUrl,
                    productId = product.Value.ProductId.ToString(),
                    unitId = product.Value.ServingUnitId.ToString(),
                    mealId = mealId!.Value.ToString(),
                    date = cellDate,
                    slotId = cellSlotId,
                    token,
                });
            Assert.Equal(200, mixedAssignStatus);

            await page.ReloadAsync();
            var mixedCard = page.Locator(".meal-card", new() { HasText = productName });
            await Assertions.Expect(mixedCard).ToBeVisibleAsync();
            await mixedCard.Locator(".mc-photo").ClickAsync();
            await Assertions.Expect(dialog).ToBeVisibleAsync();

            var recipeRow = dialog.Locator(".ed-dish", new() { HasText = "Unknown recipe" });
            var productRow = dialog.Locator(".ed-dish", new() { HasText = productName });
            await Assertions.Expect(recipeRow).ToBeVisibleAsync();
            await Assertions.Expect(productRow).ToBeVisibleAsync();

            // Acceptance: "recipe unit always reads 'srv' and is not interactive" — a static
            // label, no nested <select>, not focusable.
            var recipeUnitLabel = recipeRow.Locator(".mp-dish-qty__unit--static .mp-dish-qty__unit-label");
            await Assertions.Expect(recipeUnitLabel).ToHaveTextAsync("srv");
            await Assertions.Expect(recipeRow.Locator(".mp-dish-qty select")).ToHaveCountAsync(0);
            Assert.Equal("SPAN", await recipeUnitLabel.EvaluateAsync<string>("el => el.tagName"));
            Assert.Null(await recipeUnitLabel.GetAttributeAsync("tabindex"));

            // Acceptance: "recipe and product rows render visually identical controls (same
            // height, width class, border geometry) ... Rows align flush right." Bounding-box
            // parity is the automated stand-in for the ticket's "screenshot the real modal and
            // compare against the prototype" step.
            var recipeQtyBox = await recipeRow.Locator(".mp-dish-qty").BoundingBoxAsync();
            var productQtyBox = await productRow.Locator(".mp-dish-qty").BoundingBoxAsync();
            Assert.NotNull(recipeQtyBox);
            Assert.NotNull(productQtyBox);
            Assert.InRange(Math.Abs(recipeQtyBox!.Height - productQtyBox!.Height), 0, 1);
            Assert.InRange(
                Math.Abs((recipeQtyBox.X + recipeQtyBox.Width) - (productQtyBox.X + productQtyBox.Width)), 0, 1);

            await dialog.Locator("button.btn--secondary", new() { HasText = "Cancel" }).ClickAsync();
            await Assertions.Expect(dialog).ToBeHiddenAsync();
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-meal-plan-product-conversion.zip" });
        }
    }

    private async Task RegisterHouseholdAsync(IPage page, string email, string password)
    {
        await page.GotoAsync($"{BaseUrl}/Account/Register");
        await page.WaitForURLAsync("**/Account/Register");
        await page.FillAsync("[name='Input.HouseholdName']", "Meal Conversion Household");
        await page.FillAsync("[name='Input.Email']", email);
        await page.FillAsync("[name='Input.DisplayName']", "Meal Conversion User");
        await page.FillAsync("[name='Input.Password']", password);
        await page.ClickAsync("button[type=submit]");
        await page.WaitForURLAsync("**/Today**");
    }

    private async Task SeedProductWithConversionAsync(IPage page, string productName)
    {
        await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
        await page.WaitForURLAsync("**/Catalog/Products/Create");
        await page.FillAsync("[name='Input.Name']", productName);
        await page.SelectOptionAsync(
            "[name='Input.DefaultUnitId']", new SelectOptionValue { Label = "g — gram" });
        await page.ClickAsync("button[type=submit]:has-text('Create Product')");
        await page.WaitForURLAsync("**/Catalog/Products/*");

        await page.SelectOptionAsync(
            "select[name='ConversionInput.FromUnitId']", new SelectOptionValue { Label = "srv — serving" });
        await page.SelectOptionAsync(
            "select[name='ConversionInput.ToUnitId']", new SelectOptionValue { Label = "g — gram" });
        await page.FillAsync("input[name='ConversionInput.Factor']", "100");
        await page.RunAndWaitForResponseAsync(
            () => page.ClickAsync("button[type=submit]:has-text('Add conversion')"),
            response => response.Url.Contains("handler=AddConversion") && response.Status == 302);
        await Assertions.Expect(page.Locator("#conversions .catalog-list__primary")).ToContainTextAsync("1 srv = 100 g");
    }

    private async Task<(Guid ProductId, Guid DefaultUnitId, Guid ServingUnitId)?> GetProductDetailsAsync(string productName)
    {
        await using var conn = new NpgsqlConnection(appHost.DbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT p.id, p.default_unit_id, srv.id
              FROM catalog.products p
              JOIN catalog.units srv
                ON srv.household_id = p.household_id AND srv.symbol = 'srv'
             WHERE p.name = @name
             LIMIT 1
            """, conn);
        cmd.Parameters.AddWithValue("name", productName);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return (reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2));
    }

    private async Task<(decimal Quantity, Guid UnitId)?> GetSavedProductDishAsync(Guid productId)
    {
        await using var conn = new NpgsqlConnection(appHost.DbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT quantity, unit_id
              FROM meal_planning.planned_dish
             WHERE product_id = @productId
             ORDER BY planned_dish_id DESC
             LIMIT 1
            """, conn);
        cmd.Parameters.AddWithValue("productId", productId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return (reader.GetDecimal(0), reader.GetGuid(1));
    }

    private async Task<Guid?> GetMealIdForProductDishAsync(Guid productId)
    {
        await using var conn = new NpgsqlConnection(appHost.DbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT planned_meal_id
              FROM meal_planning.planned_dish
             WHERE product_id = @productId
             ORDER BY planned_dish_id DESC
             LIMIT 1
            """, conn);
        cmd.Parameters.AddWithValue("productId", productId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return reader.GetGuid(0);
    }
}
