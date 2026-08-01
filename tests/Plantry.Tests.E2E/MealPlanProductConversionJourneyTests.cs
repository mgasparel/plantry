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

            // Open the first empty cell in the same way the rendered grid does.
            await page.Locator(".empty-add").First.ClickAsync();
            var dialog = page.Locator("#meal-editor-dialog");
            await Assertions.Expect(dialog).ToBeVisibleAsync();

            // Search through the real editor island and add the product hit.
            var search = dialog.Locator(".dish-search input");
            await search.PressSequentiallyAsync(productName);
            var productHit = dialog.Locator(".dish-menu .dish-opt", new() { HasText = productName });
            await Assertions.Expect(productHit).ToBeVisibleAsync();
            await productHit.ClickAsync();

            var unitSelect = dialog.Locator("select.sv-unit");
            await Assertions.Expect(unitSelect).ToBeVisibleAsync();
            var unitLabels = await unitSelect.Locator("option").AllTextContentsAsync();
            Assert.Contains("srv", unitLabels);

            await dialog.Locator("input.sv-input").FillAsync("2");
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
            await Assertions.Expect(page.Locator(".meal-card", new() { HasText = productName })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".meal-card", new() { HasText = productName })).ToContainTextAsync("2 srv");
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
}
