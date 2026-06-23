using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E journey tests (Playwright) for persisted planning settings (plantry-so5.3).
///
/// Acceptance criterion L5: set a week budget WITHOUT generating → budget chip is present →
/// do a cell op → budget chip + settings survive the OOB refresh → navigate away and back →
/// settings survive navigation.
///
/// Boots the full Aspire stack via AppHostFixture.
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class PlanningSettingsJourneyTests(AppHostFixture appHost) : IAsyncLifetime
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

    /// <summary>
    /// L5 AC: set week budget without generating → chip present → cell op → chip survives → nav back → chip survives.
    ///
    /// Journey:
    ///   1. Register a fresh household.
    ///   2. Navigate to /MealPlan and open the Tune popover.
    ///   3. Set a budget ($50) without clicking Generate — the @@change on the budget input
    ///      fires persistSettings(), which POSTs ?handler=SetPlanningSettings.
    ///   4. Wait for the OOB bar refresh and assert #plan-cost-chip is still present.
    ///   5. Assign a note to a cell (cell op): GET editor → POST assign via htmx.
    ///      The Assign handler re-emits the OOB rail + bar — chip must still be present.
    ///   6. Navigate to /Recipes and back to /MealPlan — settings survive navigation.
    ///   7. Reload and assert the budget chip is still present (settings persisted to DB).
    ///   8. Open the popover and assert the budget input reflects the persisted value ($50),
    ///      confirming fix for plantry-so5.3 FIX-2 (popover reflects persisted values on render).
    /// </summary>
    [Fact(DisplayName = "L5: set week budget (no generate) → chip present → cell op + nav → settings survive (plantry-so5.3)")]
    public async Task SetWeekBudget_NoGenerate_SettingsSurviveCellOpAndNavigation()
    {
        var uniqueEmail = $"e2e-budg-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── 1. Register ───────────────────────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", "Budget E2E Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "Budget User");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── 2. Navigate to Meal Plan ──────────────────────────────────────────
            await page.GetByRole(AriaRole.Link, new() { Name = "Meal Plan" }).First.ClickAsync();
            await page.WaitForURLAsync("**/MealPlan**");
            await Assertions.Expect(page.Locator(".wkgrid")).ToBeVisibleAsync();

            // The budget chip should be present on the plan bar
            await Assertions.Expect(page.Locator("#plan-cost-chip")).ToBeVisibleAsync();

            // ── 3. Open Tune popover and set a weekly budget ($50) ────────────────
            // The af-caret button toggles tuneOpen, revealing .tune-pop.
            var caretButton = page.Locator("button.af-caret[aria-label='Tune auto-fill']");
            await Assertions.Expect(caretButton).ToBeVisibleAsync();
            await caretButton.ClickAsync();

            // Wait for the tune popover to appear (x-show="tuneOpen")
            var tunePop = page.Locator(".tune-pop");
            await Assertions.Expect(tunePop).ToBeVisibleAsync();

            // Set the weekly budget to 50 WITHOUT clicking Generate.
            // The budget number input has @@change="persistSettings(...)" which posts
            // ?handler=SetPlanningSettings when the value changes.
            var budgetInput = page.Locator(".tune-budget input[type='number']");
            await Assertions.Expect(budgetInput).ToBeVisibleAsync();
            await budgetInput.FillAsync("50");

            // Trigger @change by tabbing away — this fires persistSettings() which posts
            // ?handler=SetPlanningSettings. htmx processes the OOB response updating the bar.
            await page.RunAndWaitForResponseAsync(
                async () => await budgetInput.PressAsync("Tab"),
                r => r.Url.Contains("handler=SetPlanningSettings") && r.Status == 200);

            // ── 4. Assert #plan-cost-chip is still present after SetPlanningSettings ─
            await Assertions.Expect(page.Locator("#plan-cost-chip")).ToBeVisibleAsync();

            // ── 5. Cell op: assign a note to the first empty cell ────────────────
            // This triggers the AssignJson handler which re-emits the OOB rail + bar.
            // The budget chip must survive this OOB refresh (via island DOM swap).

            // Extract date+slotId from the first empty-add button's onclick attribute.
            // After island port (plantry-2zvm.4): empty-add uses onclick openEditor() not hx-get.
            var firstEmptyAddOnclick = await page.Locator(".empty-add").First.GetAttributeAsync("onclick");
            Assert.NotNull(firstEmptyAddOnclick);
            var cellM = System.Text.RegularExpressions.Regex.Match(
                firstEmptyAddOnclick!, @"openEditor\('([^']+)',\s*'([^']+)',\s*null\)");
            Assert.True(cellM.Success, $"Could not parse openEditor: {firstEmptyAddOnclick}");
            var cellDate2 = cellM.Groups[1].Value;
            var cellSlotId2 = cellM.Groups[2].Value;

            // POST AssignJson (note "Takeout") and apply the mutation. The barNav swap
            // carried by AssignJson re-emits #plan-cost-chip; this validates that the
            // budget chip survives a cell mutation's OOB barNav refresh.
            // Use the island bridge (applyMutation = applyMutationResult) if mounted, else inline.
            var assignJsonUrl2 = $"{BaseUrl}/MealPlan?handler=AssignJson";
            var assignStatus2 = await page.EvaluateAsync<int>(@"
                async (args) => {
                    const token = document.querySelector('input[name=""__RequestVerificationToken""]')?.value ?? '';
                    const body = JSON.stringify({
                        mode: 'note',
                        note: 'Takeout',
                        dishes: [],
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
                            'RequestVerificationToken': token,
                            'X-Requested-With': 'XMLHttpRequest'
                        },
                        body
                    });
                    if (!r.ok) return r.status;
                    const data = await r.json();
                    if (data.error) return -1;
                    if (window.__mealPlannerIsland && window.__mealPlannerIsland.applyMutation) {
                        window.__mealPlannerIsland.applyMutation(data);
                    } else {
                        // Inline fallback: swap rail + barNav fragments into live DOM.
                        if (data.railHtml) {
                            const railEl = document.getElementById('plan-rail');
                            if (railEl) railEl.outerHTML = data.railHtml;
                        }
                        if (data.barNavHtml) {
                            const tmp = document.createElement('div');
                            tmp.innerHTML = data.barNavHtml;
                            for (const el of Array.from(tmp.children)) {
                                const existing = el.id && document.getElementById(el.id);
                                if (existing) existing.outerHTML = el.outerHTML;
                            }
                        }
                        if (data.cellHtml) {
                            const m = data.cellHtml.match(/id=""(cell-[^""]+)""/);
                            if (m) { const el = document.getElementById(m[1]); if (el) el.outerHTML = data.cellHtml; }
                        }
                    }
                    return r.status;
                }", new { url = assignJsonUrl2, date = cellDate2, slotId = cellSlotId2 });
            Assert.Equal(200, assignStatus2);

            // Budget chip must still be present after the OOB bar refresh from Assign
            await Assertions.Expect(page.Locator("#plan-cost-chip")).ToBeVisibleAsync();

            // ── 6. Navigate away (to Recipes) and back ────────────────────────────
            await page.GetByRole(AriaRole.Link, new() { Name = "Recipes" }).First.ClickAsync();
            await page.WaitForURLAsync("**/Recipes**");

            await page.GetByRole(AriaRole.Link, new() { Name = "Meal Plan" }).First.ClickAsync();
            await page.WaitForURLAsync("**/MealPlan**");
            await Assertions.Expect(page.Locator(".wkgrid")).ToBeVisibleAsync();

            // Budget chip still present after navigation
            await Assertions.Expect(page.Locator("#plan-cost-chip")).ToBeVisibleAsync();

            // ── 7. Hard reload to confirm settings persisted to DB ─────────────────
            await page.ReloadAsync();
            await Assertions.Expect(page.Locator(".wkgrid")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#plan-cost-chip")).ToBeVisibleAsync();

            // ── 8. Open popover and confirm budget reflects persisted value ($50) ─
            // This verifies FIX-2: __planTuneCfg.budget is seeded from the persisted
            // WeekBudgetTarget so the Alpine component initialises budget=50, not 0.
            var caretButton2 = page.Locator("button.af-caret[aria-label='Tune auto-fill']");
            await caretButton2.ClickAsync();
            var tunePop2 = page.Locator(".tune-pop");
            await Assertions.Expect(tunePop2).ToBeVisibleAsync();

            var budgetInput2 = page.Locator(".tune-budget input[type='number']");
            await Assertions.Expect(budgetInput2).ToBeVisibleAsync();
            // The budget input should show the persisted value (50), not 0.
            var budgetValue = await budgetInput2.InputValueAsync();
            Assert.Equal("50", budgetValue);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-planning-settings-journey.zip" });
        }
    }
}
