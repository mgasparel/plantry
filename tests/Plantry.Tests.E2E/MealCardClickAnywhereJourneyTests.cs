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
///   AC4 — the card is keyboard-focusable and Enter opens the panel.
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

            var productId = await GetProductIdAsync(productName);
            Assert.True(productId.HasValue, $"Product '{productName}' not found in catalog.");

            // ── Assign a two-dish meal: one recipe dish (renders the Cook deep-link) + one
            // product dish (renders the Eat button) — both pending, so both action rows render
            // on the strip (plantry-0eut). The recipe id is a fixed placeholder (mirrors the
            // existing TwoDishAssign_ViaFetch_MealCardAppearsOnReload journey) — recipe dishes are
            // not existence-validated on assign, only rendered via the name-resolution fallback. ──
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
                            { kind: 'recipe', itemId: '00000000-0000-0000-0000-000000000099', servings: 2 },
                            { kind: 'product', itemId: args.productId, servings: 1 }
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
                }", new { url = assignUrl, productId = productId!.Value.ToString(), date, slotId, token });
            Assert.Equal(200, assignStatus);

            await page.GotoAsync($"{BaseUrl}/MealPlan");
            await page.WaitForURLAsync("**/MealPlan**");
            var card = page.Locator(".meal-card:not(.note)").First;
            await Assertions.Expect(card).ToBeVisibleAsync();
            var dialog = page.Locator("#meal-editor-dialog");

            // ── AC1: clicking the card body (a dish name — not a nested <a>/<button>) opens
            // the editor panel via the card-level onclick bridge. ──
            await card.Locator(".md-name").First.ClickAsync();
            await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = 15_000 });

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

            // ── AC4: the card is keyboard-focusable (role="button" tabindex="0") and Enter
            // opens the editor panel. ──
            await page.GotoAsync($"{BaseUrl}/MealPlan");
            await page.WaitForURLAsync("**/MealPlan**");
            var card2 = page.Locator(".meal-card:not(.note)").First;
            await Assertions.Expect(card2).ToBeVisibleAsync();
            await card2.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = 15_000 });
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-mealcard-clickanywhere.zip" });
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task<Guid?> GetProductIdAsync(string productName)
    {
        await using var conn = new NpgsqlConnection(appHost.DbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id FROM catalog.products WHERE name = @n LIMIT 1", conn);
        cmd.Parameters.AddWithValue("@n", productName);
        return await cmd.ExecuteScalarAsync() as Guid?;
    }
}
