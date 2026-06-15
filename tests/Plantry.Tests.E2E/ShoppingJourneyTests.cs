using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E journey for the Shopping list (P2-Sc, SPEC §3a-3e):
///   register → navigate to Shopping → add a product item → add a free-text item
///   → check one item off → clear checked items → assert final state.
///
/// No seeded catalog product is required for the free-text path; for the product path
/// we first create a catalog product via the Catalog UI, then use the product search
/// to add it to the shopping list.
///
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class ShoppingJourneyTests(AppHostFixture appHost) : IAsyncLifetime
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

    [Fact(DisplayName = "Shopping: add product item + free-text item → check one off → clear checked → list shows only unchecked")]
    public async Task ShoppingJourney_AddCheckClear()
    {
        var uniqueEmail = $"shop-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        // Unique product name per run avoids cross-test catalog pollution.
        var productName = $"Shop Milk {Guid.NewGuid():N}"[..24];
        const string freeTextName = "Sriracha sauce";

        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── Step 1: Register a new household ─────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", "Shopping Journey Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "Shop User");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── Step 2: Seed a catalog product so it's searchable on the list ────
            await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
            await page.WaitForURLAsync("**/Catalog/Products/Create");
            await page.FillAsync("[name='Input.Name']", productName);
            // Select the first available unit (any unit works for this journey).
            await page.SelectOptionAsync("[name='Input.DefaultUnitId']",
                new SelectOptionValue { Index = 1 });
            await page.ClickAsync("button[type=submit]");
            // After create, expect redirect to products list or detail.
            await page.WaitForURLAsync("**/Catalog/**");

            // ── Step 3: Navigate to Shopping ─────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Shopping");
            await page.WaitForURLAsync("**/Shopping**");

            // ── Step 4: Add the free-text item ────────────────────────────────────
            await page.FillAsync("[name='Input.FreeText']", freeTextName);
            await page.ClickAsync("button[type=submit]");
            // Wait for htmx swap to refresh the list.
            await page.WaitForSelectorAsync("#shopping-list");

            // The free-text item should appear in the Uncategorized group.
            var listText = await page.Locator("#shopping-list").TextContentAsync();
            Assert.Contains(freeTextName, listText, StringComparison.OrdinalIgnoreCase);

            // ── Step 5: Add the product item via searchable-select ────────────────
            // The searchable-select widget is driven by a hidden input + a text input.
            // Type into the visible search input to trigger the filter, then select.
            // We reload the page first so the form is clear.
            await page.GotoAsync($"{BaseUrl}/Shopping");
            await page.WaitForURLAsync("**/Shopping**");

            // Locate the searchable-select's text input for the product field.
            // The tag helper renders: <input type="text" class="field__input" role="combobox" ...>
            var productSearchInput = page.Locator("[role='combobox']").First;
            await productSearchInput.FillAsync(productName[..4]); // type first 4 chars
            // Wait for the dropdown listbox options to appear.
            await page.WaitForSelectorAsync(".searchable-select__listbox [role='option']");
            // Click the matching option.
            await page.Locator(".searchable-select__listbox [role='option']",
                new() { HasText = productName }).First.ClickAsync();
            await page.ClickAsync("button[type=submit]");
            await page.WaitForSelectorAsync("#shopping-list");

            listText = await page.Locator("#shopping-list").TextContentAsync();
            Assert.Contains(productName, listText, StringComparison.OrdinalIgnoreCase);

            // ── Step 6: Check off the free-text item ──────────────────────────────
            // Both items should now be on the list. Check the free-text item's checkbox.
            // The checkbox triggers an htmx POST on change.
            var checkbox = page.Locator(".shopping-item__checkbox").First;
            await checkbox.CheckAsync();
            // Wait for htmx to swap the list.
            await page.WaitForResponseAsync(r => r.Url.Contains("CheckOff"));

            // After check-off, the checked item should have the --checked class.
            var checkedItem = page.Locator(".shopping-item--checked");
            Assert.True(await checkedItem.CountAsync() >= 1, "At least one item should be checked.");

            // ── Step 7: Clear checked items ───────────────────────────────────────
            var clearBtn = page.Locator(".shopping-list__actions button");
            await clearBtn.ClickAsync();
            await page.WaitForSelectorAsync("#shopping-list");

            // After clearing, no item should have the --checked class.
            Assert.Equal(0, await page.Locator(".shopping-item--checked").CountAsync());

            // At least one item remains (the product item, which was not checked).
            Assert.True(
                await page.Locator(".shopping-item").CountAsync() >= 1,
                "At least one unchecked item should remain after clearing.");
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "shopping-trace.zip" });
        }
    }
}
