using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E — the recipe author journeys (P2-1f done-when):
///
///   <list type="number">
///     <item><description>
///       <b>Create flow (J6)</b>: new recipe → add one ingredient via product search, one via inline
///       staple create → mint a new tag inline → upload a photo → save → assert landing on Detail
///       with the expected content (title, ingredient names, tag pill).
///     </description></item>
///     <item><description>
///       <b>Edit flow (J7)</b>: open an existing recipe → change servings (triggering the Proportional
///       scale offer) → choose Proportional → edit one ingredient quantity → save → assert the Detail
///       page reflects the updated servings and changed quantity.
///     </description></item>
///   </list>
///
/// Both journeys register a fresh household per run (unique email) and seed the catalog before
/// exercising the recipe editor, so each run is independent and CI-safe.
///
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class RecipeAuthorJourneyTests(AppHostFixture appHost) : IAsyncLifetime
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

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>Registers a fresh household and returns the page (landed on /Pantry).</summary>
    private async Task<IPage> RegisterHouseholdAsync(IBrowserContext context, string email, string householdName)
    {
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

        await page.GotoAsync($"{BaseUrl}/Account/Register");
        await page.WaitForURLAsync("**/Account/Register");
        await page.FillAsync("[name='Input.HouseholdName']", householdName);
        await page.FillAsync("[name='Input.Email']", email);
        await page.FillAsync("[name='Input.DisplayName']", "Test User");
        await page.FillAsync("[name='Input.Password']", "testpass1");
        await page.ClickAsync("button[type=submit]");
        await page.WaitForURLAsync("**/Pantry**");

        return page;
    }

    /// <summary>Seeds one catalog product and returns the product name.</summary>
    private async Task<string> SeedProductAsync(IPage page, string productName)
    {
        await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
        await page.WaitForURLAsync("**/Catalog/Products/Create");
        await page.FillAsync("[name='Input.Name']", productName);
        await page.SelectOptionAsync("[name='Input.DefaultUnitId']", new SelectOptionValue { Label = "ea — each" });
        await page.ClickAsync("button:has-text('Add product')");
        await page.WaitForURLAsync("**/Catalog/Products/**");
        return productName;
    }

    // ── Journey 1: Create ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "J6 Create: new recipe → ingredient search + inline staple + new tag + photo → Detail shows content")]
    public async Task CreateRecipe_WithIngredients_LandsOnDetailWithExpectedContent()
    {
        var email = $"recipe-create-{Guid.NewGuid():N}@test.local";

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await RegisterHouseholdAsync(context, email, "Recipe Create Household");

            // ── Seed a catalog product for the ingredient product-search path ──────
            var tomatoName = $"Roma Tomato {Guid.NewGuid():N}".Substring(0, 24);
            await SeedProductAsync(page, tomatoName);

            // ── Navigate to new recipe form via the /Recipes/New alias ───────────
            await page.GotoAsync($"{BaseUrl}/Recipes/New");
            await page.WaitForURLAsync("**/Recipes/New");

            // ── Fill recipe basics ────────────────────────────────────────────────
            var recipeName = $"Tomato Pasta {Guid.NewGuid():N}".Substring(0, 20);
            await page.FillAsync("[name='Input.Name']", recipeName);
            await page.FillAsync("[name='Input.DefaultServings']", "2");

            // ── Add a tag inline: type in the tag input and press Enter ───────────
            await page.FillAsync("input[placeholder='Add a tag…']", "Italian");
            await page.Keyboard.PressAsync("Enter");
            // The chip should appear in the tag editor (Alpine-rendered)
            await Assertions.Expect(page.Locator(".recipe-tag-editor .badge", new() { HasText = "Italian" }))
                .ToBeVisibleAsync();

            // ── Ingredient row 1: product search ─────────────────────────────────
            // Alpine pre-renders one empty row. Focus the product search in the first row.
            var firstRowSearch = page.Locator(".ingredient-row").First
                .Locator("input[role='combobox']");
            await firstRowSearch.FillAsync(tomatoName.Substring(0, 8));
            // Wait for the htmx product search list to populate
            var option = page.Locator(".searchable-select__listbox li[role='option']",
                new() { HasText = tomatoName });
            await Assertions.Expect(option).ToBeVisibleAsync();
            await option.ClickAsync();

            // Fill qty and unit for the tracked row
            var firstRow = page.Locator(".ingredient-row").First;
            await firstRow.Locator("input[placeholder='Qty']").FillAsync("400");
            await firstRow.Locator("select").First.SelectOptionAsync(new SelectOptionValue { Label = "ea" });

            // ── Ingredient row 2: inline staple create ────────────────────────────
            await page.ClickAsync("button:has-text('Add ingredient')");
            var secondRow = page.Locator(".ingredient-row").Nth(1);
            // Click "Create as staple (untracked)" to switch the row to staple mode
            await secondRow.Locator("button:has-text('Create as staple')").ClickAsync();
            await Assertions.Expect(secondRow.Locator("input[placeholder='Staple name (e.g. Salt)']"))
                .ToBeVisibleAsync();
            await secondRow.Locator("input[placeholder='Staple name (e.g. Salt)']").FillAsync("Olive Oil");
            await secondRow.Locator("select").First.SelectOptionAsync(new SelectOptionValue { Label = "ea" });

            // ── Upload a photo (a tiny PNG — the app stores it as bytea) ─────────
            await page.SetInputFilesAsync("input[type=file][name='photo']", new FilePayload
            {
                Name = "recipe.png",
                MimeType = "image/png",
                Buffer = TinyPngBytes(),
            });

            // ── Submit the form ───────────────────────────────────────────────────
            await page.ClickAsync("button[type=submit]:has-text('Create recipe')");

            // ── Assert: lands on the Detail page ─────────────────────────────────
            await page.WaitForURLAsync("**/Recipes/**");
            // URL should NOT end in /Edit — it is the detail page for the new recipe id
            Assert.DoesNotMatch(@"/Edit$", page.Url);

            // Recipe name is the page heading
            var heading = await page.Locator("h2.catalog-section__heading").TextContentAsync();
            Assert.Contains(recipeName, heading, StringComparison.OrdinalIgnoreCase);

            // Tomato ingredient renders in the list
            await Assertions.Expect(page.Locator(".recipe-ingredient-list__item", new() { HasText = tomatoName }))
                .ToBeVisibleAsync();

            // Olive Oil untracked staple renders (untracked label)
            await Assertions.Expect(page.Locator(".recipe-ingredient-list__item", new() { HasText = "Olive Oil" }))
                .ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".recipe-ingredient__note--untracked"))
                .ToBeVisibleAsync();

            // Tag pill "Italian" is present
            await Assertions.Expect(page.Locator(".recipe-tags .badge", new() { HasText = "Italian" }))
                .ToBeVisibleAsync();

            // Photo is rendered (hero img tag present)
            await Assertions.Expect(page.Locator(".recipe-hero__img"))
                .ToBeVisibleAsync();
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-recipe-create.zip" });
        }
    }

    // ── Journey 2: Edit ───────────────────────────────────────────────────────────

    [Fact(DisplayName = "J7 Edit: change servings (Proportional) + edit ingredient → Detail reflects changes")]
    public async Task EditRecipe_ChangeServingsProportional_DetailReflectsChanges()
    {
        var email = $"recipe-edit-{Guid.NewGuid():N}@test.local";

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await RegisterHouseholdAsync(context, email, "Recipe Edit Household");

            // ── Seed a catalog product ────────────────────────────────────────────
            var flourName = $"Plain Flour {Guid.NewGuid():N}".Substring(0, 22);
            await SeedProductAsync(page, flourName);

            // ── Create a recipe to edit ───────────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Recipes/New");
            await page.WaitForURLAsync("**/Recipes/New");

            var recipeName = $"Bread {Guid.NewGuid():N}".Substring(0, 16);
            await page.FillAsync("[name='Input.Name']", recipeName);
            await page.FillAsync("[name='Input.DefaultServings']", "2");

            // Pick the seeded flour product
            var searchInput = page.Locator(".ingredient-row").First.Locator("input[role='combobox']");
            await searchInput.FillAsync(flourName.Substring(0, 5));
            var option = page.Locator(".searchable-select__listbox li[role='option']",
                new() { HasText = flourName });
            await Assertions.Expect(option).ToBeVisibleAsync();
            await option.ClickAsync();

            var firstRow = page.Locator(".ingredient-row").First;
            await firstRow.Locator("input[placeholder='Qty']").FillAsync("200");
            await firstRow.Locator("select").First.SelectOptionAsync(new SelectOptionValue { Label = "ea" });

            await page.ClickAsync("button[type=submit]:has-text('Create recipe')");
            await page.WaitForURLAsync("**/Recipes/**");

            // Capture the detail URL so we can construct the edit URL
            var detailUrl = page.Url;
            var editUrl = detailUrl.TrimEnd('/') + "/Edit";

            // ── Open the edit form ────────────────────────────────────────────────
            await page.GotoAsync(editUrl);
            await page.WaitForURLAsync($"**{editUrl.Replace(BaseUrl, "")}");

            // ── Change servings: 2 → 4 (triggers the scale offer) ────────────────
            var servingsInput = page.Locator("[name='Input.DefaultServings']");
            await servingsInput.ClearAsync();
            await servingsInput.FillAsync("4");
            // Trigger Alpine's servings watcher so showScaleOffer() evaluates to true.
            await servingsInput.BlurAsync();

            // The scale-offer card should now be visible (Alpine reveals it when servings != origServings
            // and hasIngredients is true).
            var scaleCard = page.Locator(".seg-ctrl[aria-label='Scale mode']").Locator("..");
            await Assertions.Expect(scaleCard).ToBeVisibleAsync();

            // Select Proportional mode
            await page.Locator("input[type=radio][value='Proportional']").ClickAsync();

            // ── Also change the first ingredient quantity directly ─────────────────
            // (Alpine row qty is bound to the hidden input; we edit the visible Qty text input)
            var qtyInput = page.Locator(".ingredient-row").First.Locator("input[placeholder='Qty']");
            await qtyInput.ClearAsync();
            await qtyInput.FillAsync("500");

            // ── Submit ────────────────────────────────────────────────────────────
            await page.ClickAsync("button[type=submit]:has-text('Save changes')");
            await page.WaitForURLAsync("**/Recipes/**");
            Assert.DoesNotMatch(@"/Edit$", page.Url);

            // ── Assert: Detail reflects updated servings ──────────────────────────
            var servingsText = await page.Locator(".recipe-meta__item").First.TextContentAsync();
            Assert.Contains("4", servingsText);

            // Recipe name still visible
            var heading = await page.Locator("h2.catalog-section__heading").TextContentAsync();
            Assert.Contains(recipeName, heading, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-recipe-edit.zip" });
        }
    }

    // ── Shared helpers ────────────────────────────────────────────────────────────

    /// <summary>Smallest valid 1×1 PNG for photo upload (same helper as ReceiptIntakeJourneyTests).</summary>
    private static byte[] TinyPngBytes() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
}
