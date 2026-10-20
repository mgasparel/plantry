using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Npgsql;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E journey test for plantry-ely3: clicking anywhere on a populated meal card opens the
/// details/editor panel — not only the hover-revealed pencil. Exercises the acceptance criteria that
/// require a real browser click/keyboard simulation (a rendered-markup assertion cannot prove this):
///   AC1 — clicking the card body (a dish name) opens the editor panel.
///   AC2 — the Eat button still fires its own hx-post (swap to the Eaten row) and does NOT also open
///         the panel; the Cook deep-link still navigates to /Recipes/{id}/Cook.
///   AC4 — the card's hidden primary-activation button is keyboard-focusable and Enter opens the
///         panel (plantry-bg2v: role="button" moved off the card div onto a dedicated hidden
///         &lt;button&gt;, since a button role can't legally wrap other interactive elements).
/// AC3 (drag unaffected) and AC5 (empty/ghost cells unchanged) touch no code in this change — the
/// ondragstart wiring and the empty/ghost cell templates are untouched — so they are not re-asserted
/// here; the existing WeekGridJourneyTests drag/relocate journeys already cover AC3's ondragstart path.
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class MealCardClickAnywhereJourneyTests(AppHostFixture appHost) : IAsyncLifetime
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

    [Fact(DisplayName = "Meal card click-anywhere: body click opens editor; Eat fires without opening it; Cook link still navigates; Enter opens it")]
    public async Task ClickAnywhereOpensEditor_NestedActionsKeepOwnBehavior_KeyboardActivates()
    {
        var uniqueEmail = $"e2e-cardclick-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";
        var productName = $"Click Anywhere Milk {Guid.NewGuid():N}"[..28];

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });
        await context.AddInitScriptAsync(@"(() => {
            const callsKey = '__plantryTestOpenEditorCalls';
            let island;
            Object.defineProperty(window, '__mealPlannerIsland', {
                configurable: true,
                get: () => island,
                set: value => {
                    if (value && typeof value.openEditor === 'function' && !value.__plantryTestWrapped) {
                        const openEditor = value.openEditor;
                        value.openEditor = function (...args) {
                            const calls = JSON.parse(sessionStorage.getItem(callsKey) || '[]');
                            calls.push(args);
                            sessionStorage.setItem(callsKey, JSON.stringify(calls));
                            return openEditor.apply(this, args);
                        };
                        Object.defineProperty(value, '__plantryTestWrapped', { value: true });
                    }
                    island = value;
                }
            });
        })();");

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── Register a fresh household ────────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", "Card Click Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "Click User");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── Seed a real catalog product ────────────────────────────────────────
            // AssignMealService validates product-kind dishes via IMealPlanCatalogProductReader
            // against the real Catalog store (ExistsAsync / IsPlannableAsync) — a fake product id
            // would be rejected on assign, so the Eat-button dish needs a genuine product.
            await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
            await page.WaitForURLAsync("**/Catalog/Products/Create");
            await page.FillAsync("[name='Input.Name']", productName);
            await page.SelectOptionAsync("[name='Input.DefaultUnitId']", new SelectOptionValue { Label = "g — gram" });
            await page.ClickAsync("button[type=submit]:has-text('Create Product')");
            await page.WaitForURLAsync("**/Catalog/**");

            var product = await GetProductDetailsAsync(productName);
            Assert.True(product.HasValue, $"Product '{productName}' not found in catalog.");

            var recipeName = $"Card Link Recipe {Guid.NewGuid():N}"[..24];
            var recipeId = await CreateRecipeAsync(page, recipeName, productName);

            // ── Assign a two-dish meal: one real recipe dish (name link + Cook action) and one
            // product dish (Eat button), both pending. ──
            await page.GetByRole(AriaRole.Link, new() { Name = "Meal Plan" }).First.ClickAsync();
            await page.WaitForURLAsync("**/MealPlan**");
            await Assertions.Expect(page.Locator(".wkgrid")).ToBeVisibleAsync();

            var onclick = await page.Locator(".empty-add").First.GetAttributeAsync("onclick");
            Assert.NotNull(onclick);
            var cellMatch = Regex.Match(onclick!, @"openEditor\('([^']+)',\s*'([^']+)',\s*null\)");
            Assert.True(cellMatch.Success, $"Could not parse openEditor from onclick: {onclick}");
            var date = cellMatch.Groups[1].Value;
            var slotId = cellMatch.Groups[2].Value;

            var token = await page.Locator("input[name=__RequestVerificationToken]").First.GetAttributeAsync("value") ?? "";
            var assignUrl = $"{BaseUrl}/MealPlan?handler=AssignJson";
            var assignStatus = await page.EvaluateAsync<int>(@"
                async (args) => {
                    const body = JSON.stringify({
                        mode: 'dishes',
                        dishes: [
                            { kind: 'recipe', itemId: args.recipeId, servings: 2 },
                            { kind: 'product', itemId: args.productId, quantity: 1, unitId: args.unitId }
                        ],
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
                }", new { url = assignUrl, productId = product!.Value.ProductId.ToString(), unitId = product.Value.DefaultUnitId.ToString(), recipeId, date, slotId, token });
            Assert.Equal(200, assignStatus);

            await page.GotoAsync($"{BaseUrl}/MealPlan");
            await page.WaitForURLAsync("**/MealPlan**");
            var card = page.Locator(".meal-card:not(.note)").First;
            await Assertions.Expect(card).ToBeVisibleAsync();
            var dialog = page.Locator("#meal-editor-dialog");

            // Recipe names are native same-tab links. On a narrow viewport they retain their
            // underline and visible keyboard focus. Record production openEditor calls before
            // navigation unloads the page so link activation cannot hide a card-editor call.
            await page.SetViewportSizeAsync(390, 844);
            var recipeLink = card.Locator("a.md-recipe-link");
            await Assertions.Expect(recipeLink).ToHaveAttributeAsync("href", $"/Recipes/{recipeId}");
            var linkStyle = await recipeLink.EvaluateAsync<string>("el => getComputedStyle(el).textDecorationLine");
            Assert.Contains("underline", linkStyle);
            await recipeLink.FocusAsync();
            await page.Keyboard.PressAsync("Tab");
            await page.Keyboard.PressAsync("Shift+Tab");
            var focusOutline = await recipeLink.EvaluateAsync<string>("el => getComputedStyle(el).outlineStyle");
            Assert.NotEqual("none", focusOutline);

            await page.EvaluateAsync("() => sessionStorage.removeItem('__plantryTestOpenEditorCalls')");
            await recipeLink.ClickAsync();
            await page.WaitForURLAsync($"**/Recipes/{recipeId}");
            Assert.Equal($"{new Uri(BaseUrl).GetLeftPart(UriPartial.Authority)}/Recipes/{recipeId}", page.Url);
            Assert.Equal(0, await GetRecordedOpenEditorCallCountAsync(page));

            // Enter activates the same link through the keyboard. Both activations must navigate
            // in the same tab without calling openEditor before the meal page unloads.
            await page.GotoAsync($"{BaseUrl}/MealPlan");
            await page.WaitForURLAsync("**/MealPlan**");
            card = page.Locator(".meal-card:not(.note)").First;
            recipeLink = card.Locator("a.md-recipe-link");
            await page.EvaluateAsync("() => sessionStorage.removeItem('__plantryTestOpenEditorCalls')");
            await recipeLink.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForURLAsync($"**/Recipes/{recipeId}");
            Assert.Equal($"{new Uri(BaseUrl).GetLeftPart(UriPartial.Authority)}/Recipes/{recipeId}", page.Url);
            Assert.Equal(0, await GetRecordedOpenEditorCallCountAsync(page));

            await page.GotoAsync($"{BaseUrl}/MealPlan");
            await page.WaitForURLAsync("**/MealPlan**");
            card = page.Locator(".meal-card:not(.note)").First;

            // ── AC1: clicking the product dish name (plain text, not an interactive recipe link
            // or nested button) opens the editor panel via the card-level onclick bridge. ──
            await page.EvaluateAsync("() => sessionStorage.removeItem('__plantryTestOpenEditorCalls')");
            await card.Locator(".md-name:not(a)").First.ClickAsync();
            await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = 15_000 });
            Assert.Equal(1, await GetRecordedOpenEditorCallCountAsync(page));

            // Close via Escape — meal-planner.js's global keydown handler closes on Escape.
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(dialog).ToBeHiddenAsync(new() { Timeout = 10_000 });

            // ── AC2 (Eat): clicking the Eat button fires its own hx-post — proving the card-level
            // guard did NOT swallow the click — and does NOT also open the editor panel. (The
            // product is deliberately stockless, so the write is the documented shortfall-tolerant
            // no-op — MealPlanEatWriterAdapter's "Inventory.NoStock" path — and the dish legitimately
            // stays pending; that inventory behavior is out of scope here. What this asserts is:
            // the hx-post fired and completed (200), and the editor did not also open.) ──
            var eatBtn = card.Locator(".mc-cook-act.eat");
            await Assertions.Expect(eatBtn).ToBeVisibleAsync();
            await page.RunAndWaitForResponseAsync(
                () => eatBtn.ClickAsync(),
                resp => resp.Url.Contains("MealPlan") && resp.Url.Contains("handler=Eat") && resp.Status == 200);
            await Assertions.Expect(dialog).ToBeHiddenAsync();

            // ── AC2 (Cook): clicking the Cook deep-link still navigates to /Recipes/{id}/Cook (the
            // card-level guard bails when the click target is nested inside an <a>). ──
            var cookLink = card.Locator("a.mc-cook-act");
            await Assertions.Expect(cookLink).ToBeVisibleAsync();
            await cookLink.ClickAsync();
            await page.WaitForURLAsync("**/Recipes/*/Cook**");
            Assert.Matches(new Regex("/Recipes/[0-9a-fA-F-]+/Cook"), page.Url);

            // ── AC4: the card's hidden primary-activation button is keyboard-focusable and Enter
            // opens the editor panel (plantry-bg2v: role="button" moved off the card div itself,
            // onto a dedicated hidden <button>, since a button role can't legally wrap other
            // interactive elements). ──
            await page.GotoAsync($"{BaseUrl}/MealPlan");
            await page.WaitForURLAsync("**/MealPlan**");
            var card2 = page.Locator(".meal-card:not(.note)").First;
            await Assertions.Expect(card2).ToBeVisibleAsync();
            await card2.Locator(".mc-open-details").FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = 15_000 });
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-mealcard-clickanywhere.zip" });
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static Task<int> GetRecordedOpenEditorCallCountAsync(IPage page) =>
        page.EvaluateAsync<int>("() => JSON.parse(sessionStorage.getItem('__plantryTestOpenEditorCalls') || '[]').length");

    private async Task<(Guid ProductId, Guid DefaultUnitId)?> GetProductDetailsAsync(string productName)
    {
        await using var conn = new NpgsqlConnection(appHost.DbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, default_unit_id FROM catalog.products WHERE name = @n LIMIT 1", conn);
        cmd.Parameters.AddWithValue("@n", productName);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        return (reader.GetGuid(0), reader.GetGuid(1));
    }

    private async Task<string> CreateRecipeAsync(IPage page, string recipeName, string productName)
    {
        await page.GotoAsync($"{BaseUrl}/Recipes/New");
        await page.WaitForURLAsync("**/Recipes/New");
        await page.FillAsync("[name='Input.Name']", recipeName);
        await page.FillAsync("[name='Input.DefaultServings']", "2");
        await page.ClickAsync("button:has-text('Add ingredient')");
        var sheet = page.Locator("#recipe-editor .sheet");
        await Assertions.Expect(sheet).ToBeVisibleAsync();
        await sheet.Locator("input[role='combobox']").PressSequentiallyAsync(productName[..8]);
        await Assertions.Expect(sheet.Locator(".searchable-select__listbox li[role='option']", new() { HasText = productName })).ToBeVisibleAsync();
        await sheet.Locator(".searchable-select__listbox li[role='option']", new() { HasText = productName }).ClickAsync();
        await sheet.Locator("input[type='number']:visible").FillAsync("1");
        await sheet.Locator("select:visible").SelectOptionAsync(new SelectOptionValue { Label = "g" });
        await sheet.Locator(".sheet__actions button.btn--primary").First.ClickAsync();
        await Assertions.Expect(sheet).Not.ToBeVisibleAsync();
        await page.ClickAsync("button[type=submit]:has-text('Create Recipe')");
        await page.WaitForURLAsync("**/Recipes/*", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        var id = new Uri(page.Url).AbsolutePath.Split('/').Last();
        Assert.True(Guid.TryParse(id, out _), $"Expected a recipe details URL, got {page.Url}");
        return id;
    }
}
