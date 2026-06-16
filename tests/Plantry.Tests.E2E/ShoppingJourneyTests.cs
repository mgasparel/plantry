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
            // Scope to the create button by text — a bare button[type=submit] selector
            // also matches the sidebar "Sign out" button (rendered before the form in the
            // banded-IA layout), and page.ClickAsync picks the first DOM match.
            await page.ClickAsync("button[type=submit]:has-text('Add product')");
            // After create, expect redirect to products list or detail.
            await page.WaitForURLAsync("**/Catalog/**");

            // ── Step 3: Navigate to Shopping ─────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Shopping");
            await page.WaitForURLAsync("**/Shopping**");

            // ── Step 4: Add the free-text item ────────────────────────────────────
            await page.FillAsync("[name='Input.FreeText']", freeTextName);
            // Use the "Add to list" submit button by its text to avoid ambiguity with
            // the quantity-stepper save buttons that also have type=submit.
            await page.ClickAsync("button[type=submit]:has-text('Add to list')");

            // The free-text item should appear after the htmx swap. Use a web-first
            // assertion so it retries until the AddItem response has swapped #shopping-list —
            // WaitForSelector returns immediately because the element already exists, so a
            // one-shot TextContent read races the swap.
            await Assertions.Expect(page.Locator("#shopping-list")).ToContainTextAsync(freeTextName);

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
            await page.ClickAsync("button[type=submit]:has-text('Add to list')");
            await Assertions.Expect(page.Locator("#shopping-list")).ToContainTextAsync(productName);

            // ── Step 6: Check off the free-text item ──────────────────────────────
            // After the Shopping List visual redesign (plantry-ah3), the check control
            // is a <button class="sl-check"> (not a checkbox input). The item row is
            // <div class="sl-item"> and gains class "done" when checked.
            // The button fires hx-post to the CheckOff handler on click.
            var checkBtn = page.Locator(".sl-check").First;
            await checkBtn.ClickAsync();

            // After check-off, the checked row gains class "done". Web-first assertion retries
            // until htmx has applied the CheckOff swap (waiting on the response alone races the
            // DOM mutation).
            await Assertions.Expect(page.Locator(".sl-item.done").First).ToBeVisibleAsync();

            // ── Step 7: Clear checked items ───────────────────────────────────────
            // After redesign the clear button is .sl-clear (a submit inside .sl-checked-head form).
            var clearBtn = page.Locator(".sl-clear");
            await clearBtn.ClickAsync();

            // After clearing, no item should have the "done" class. Web-first assertions
            // retry until the clear response swaps the list.
            await Assertions.Expect(page.Locator(".sl-item.done")).ToHaveCountAsync(0);

            // At least one item remains (the product item, which was not checked).
            await Assertions.Expect(page.Locator(".sl-item").First).ToBeVisibleAsync();
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "shopping-trace.zip" });
        }
    }
}
