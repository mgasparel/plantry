using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E smoke test for the Today page planned-meals band (SPEC Page 0 §0c, plantry-zp7).
///
/// Acceptance criteria verified:
///   "Cook entry points — clicking Cook from the Today band navigates to the Cook page for the planned recipe."
///
/// Journey:
///   Register a fresh household → create a recipe → navigate to /MealPlan → assign the recipe
///   to today's first slot via POST → navigate to /Today → verify the planned-meals band shows
///   the recipe → click Cook → verify the Cook page opens for that recipe.
///
/// Uses the same fetch()-based assign pattern as WeekGridJourneyTests to bypass the Preact
/// island editor UI (Alpine/Preact hydration timing is unreliable in headless CI).
///
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class TodayPlannedMealsSmokeTests(AppHostFixture appHost) : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    private string BaseUrl => appHost.BaseUrl;

    /// <summary>
    /// Matches the recipe Detail page URL (/Recipes/{guid} — not /Edit or /New).
    /// Mirrors the pattern used in RecipeAuthorJourneyTests and RecipeCookJourneyTests.
    /// </summary>
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

    [Fact(DisplayName = "Today: planned recipe appears in meals band and Cook opens the cook flow (plantry-zp7/AC)")]
    public async Task Today_PlannedMeal_RecipeAppears_AndCookOpensFlow()
    {
        var uniqueEmail = $"planned-meals-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";
        var recipeName = $"PM {Guid.NewGuid():N}".Substring(0, 15);

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
            await page.FillAsync("[name='Input.HouseholdName']", "PlannedMeals Test Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "Planned Tester");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── 2. Create a recipe with one staple ingredient ─────────────────────
            // The recipe edit form requires at least one ingredient to save.
            // We use inline staple creation (no catalog product needed) to keep the
            // test self-contained (mirrors RecipeAuthorJourneyTests.AddStapleIngredientAsync).
            await page.GotoAsync($"{BaseUrl}/Recipes/New");
            await page.WaitForURLAsync("**/Recipes/New");
            await page.FillAsync("[name='Input.Name']", recipeName);
            await page.FillAsync("[name='Input.DefaultServings']", "2");

            // Open the add-ingredient sheet, switch to tracked-product create mode (plantry-orix),
            // fill name + default unit, commit, then re-open to satisfy R5 (tracked needs qty).
            await page.ClickAsync("button:has-text('Add ingredient')");
            var ingSheet = page.Locator("#recipe-editor .sheet");
            await Assertions.Expect(ingSheet).ToBeVisibleAsync();
            // The shared search + create component's create button (plantry-gzro, trailing phrase
            // "as a new product") only appears once the user has typed a query, so type first.
            await ingSheet.Locator("input[role='combobox']").PressSequentiallyAsync("Salt");
            await ingSheet.Locator("button:has-text('as a new product')").ClickAsync();
            var nameInput = ingSheet.Locator("input[placeholder='Product name (e.g. Olive Oil)']");
            await Assertions.Expect(nameInput).ToBeVisibleAsync();
            await nameInput.FillAsync("Salt");
            // The Defaults collapsible (plantry-y53t) is OPEN by default (plantry-grvy) — the Unit
            // select is directly accessible without clicking the summary.
            var ingDefaultsSummary = ingSheet.Locator(".sheet-defaults__summary");
            await Assertions.Expect(ingDefaultsSummary).ToBeVisibleAsync();
            // Select the unit — the #create-product-unit select is visible inside the open collapsible.
            await ingSheet.Locator("#create-product-unit").SelectOptionAsync(new SelectOptionValue { Label = "ea" });
            // Use .Last to target the create-view "Create" button (plantry-nb4x two-view scaffold).
            await ingSheet.Locator(".sheet__actions button.btn--primary").Last.ClickAsync();
            await Assertions.Expect(ingSheet).Not.ToBeVisibleAsync();
            // R5: tracked ingredients require a Quantity. Re-open the row to fill qty.
            var saltRow = page.Locator(".ingredient-row", new() { HasText = "Salt" });
            await Assertions.Expect(saltRow).ToBeVisibleAsync();
            await saltRow.Locator("button[aria-label='Edit ingredient']").ClickAsync();
            await Assertions.Expect(ingSheet).ToBeVisibleAsync();
            // `:visible` targets the search-view Quantity; the create-view Quantity (plantry-guab) is x-show hidden.
            await ingSheet.Locator("input[type='number']:visible").FillAsync("1");
            await ingSheet.Locator(".sheet__actions button.btn--primary").First.ClickAsync();
            await Assertions.Expect(ingSheet).Not.ToBeVisibleAsync();

            await page.ClickAsync("button[type=submit]:has-text('Create recipe')");

            // Wait specifically for the Detail URL (/Recipes/{guid}) — the loose "**/Recipes/**"
            // also matches /Recipes/New and would return before the post-save redirect completes.
            await page.WaitForURLAsync(DetailUrlPattern);

            // Extract the recipe ID — Detail URL is /Recipes/{guid} (bare GUID, no /Details suffix).
            var recipeId = page.Url.Split('/').Last();

            // ── 3. Navigate to /MealPlan and get today's first slot info ──────────
            await page.GotoAsync($"{BaseUrl}/MealPlan");
            await page.WaitForURLAsync("**/MealPlan**");
            await Assertions.Expect(page.Locator(".wkgrid")).ToBeVisibleAsync();

            // Find the first empty cell for today (today's date in ISO yyyy-MM-dd format)
            var todayIso = DateTime.Today.ToString("yyyy-MM-dd");
            var (date, slotId) = await GetCellForTodayAsync(page, todayIso);

            // ── 4. Assign the recipe to today's slot via AssignJson (same as editor Save) ──
            var token = await GetAntiforgeryTokenAsync(page);
            var assignResult = await PostAssignRecipeAsync(page, date, slotId, recipeId, token);
            Assert.Equal(200, assignResult);

            // ── 5. Navigate to Today and verify the planned-meals band ────────────
            await page.GotoAsync($"{BaseUrl}/Today");
            await page.WaitForURLAsync("**/Today**");
            await Assertions.Expect(page.Locator(".today-wrap")).ToBeVisibleAsync();

            // The meals band must be present
            var mealsBand = page.Locator("#today-meals-band");
            await Assertions.Expect(mealsBand).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

            // The planned slot must render with the recipe name
            var plannedSlot = page.Locator(".today-meal-slot--planned", new() { HasText = recipeName });
            await Assertions.Expect(plannedSlot).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 15000 });

            // ── 6. Verify Cook button is present and click it ─────────────────────
            var cookBtn = plannedSlot.Locator(".today-meal-slot__cook");
            await Assertions.Expect(cookBtn).ToBeVisibleAsync();
            var cookHref = await cookBtn.GetAttributeAsync("href") ?? "";
            Assert.Matches(@"/Recipes/[0-9a-f-]+/Cook", cookHref);

            await cookBtn.ClickAsync();

            // The Cook page must load
            await page.WaitForURLAsync("**/Recipes/**/Cook**");
            await Assertions.Expect(page.Locator(".cook-title")).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 15000 });
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-today-planned-meals.zip" });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the first empty-add button whose onclick contains today's date.
    /// Falls back to the first empty-add button if today's date is not visible in the grid
    /// (e.g., the test is running on Sunday and the grid starts on Monday).
    /// </summary>
    private static async Task<(string date, string slotId)> GetCellForTodayAsync(IPage page, string todayIso)
    {
        // Collect all empty-add onclicks and find one for today
        var onclicks = await page.Locator(".empty-add").EvaluateAllAsync<string[]>(
            "els => els.map(el => el.getAttribute('onclick') ?? '')");

        foreach (var onclick in onclicks)
        {
            if (string.IsNullOrEmpty(onclick)) continue;
            var m = System.Text.RegularExpressions.Regex.Match(onclick,
                @"openEditor\('([^']+)',\s*'([^']+)',\s*null\)");
            if (m.Success && m.Groups[1].Value == todayIso)
                return (m.Groups[1].Value, m.Groups[2].Value);
        }

        // Fallback: use the first empty cell (may be a different day if today = weekend before plan week)
        var firstOnclick = onclicks.FirstOrDefault(o => !string.IsNullOrEmpty(o)) ?? "";
        Assert.False(string.IsNullOrEmpty(firstOnclick),
            "No empty-add buttons found on MealPlan page. Cannot assign a meal for Today test.");
        var fallback = System.Text.RegularExpressions.Regex.Match(firstOnclick,
            @"openEditor\('([^']+)',\s*'([^']+)',\s*null\)");
        Assert.True(fallback.Success, $"Could not parse openEditor from onclick: {firstOnclick}");
        return (fallback.Groups[1].Value, fallback.Groups[2].Value);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(IPage page)
    {
        return await page.Locator("input[name=__RequestVerificationToken]").First
                   .GetAttributeAsync("value") ?? "";
    }

    private async Task<int> PostAssignRecipeAsync(IPage page, string date, string slotId, string recipeId, string token)
    {
        var assignUrl = $"{BaseUrl}/MealPlan?handler=AssignJson";
        return await page.EvaluateAsync<int>(@"
            async (args) => {
                const body = JSON.stringify({
                    mode: 'dishes',
                    note: null,
                    dishes: [{ kind: 'recipe', itemId: args.recipeId, servings: 2 }],
                    att: null,
                    attendeesOverridden: false,
                    mealId: null,
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
            }", new { url = assignUrl, recipeId, date, slotId, token });
    }
}
