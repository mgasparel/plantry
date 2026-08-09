using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E journey tests (Playwright) for persisted planning settings and auto-fill hydration.
///
/// Acceptance criterion L5: configure household weights → verify the household fallback → create a
/// week override through Tune → verify the OOB replacement, a cell mutation, adjacent-week fallback,
/// navigation, and a hard reload all preserve the correct effective split.
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
    /// L5 AC: configure household defaults → create a week override without generating → the OOB
    /// replacement and a cell mutation preserve the override → adjacent navigation falls back to
    /// household defaults → navigation/reload restore the overridden week.
    ///
    /// Journey:
    ///   1. Register a fresh household.
    ///   2. Configure household defaults to 40/30/30 from the settings page.
    ///   3. Navigate to /MealPlan and verify the no-override fallback is 40/30/30.
    ///   4. Move Waste to 50 in Tune; constant-sum rebalance produces 50/25/25, and the
    ///      @@change persists it through ?handler=SetPlanningSettings.
    ///   5. Wait for the OOB bar refresh, reopen Tune, and verify the override marker and 50/25/25.
    ///   6. Assign a note to a cell; the Assign handler re-emits the OOB bar with the same override.
    ///   7. Navigate to the adjacent week and verify it falls back to household defaults; navigate back
    ///      and verify the original week's override returns.
    ///   8. Navigate away and back, reload, and verify the original week still shows 50/25/25 and the
    ///      household defaults remain 40/30/30.
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

            // ── 2. Configure distinctive household defaults (40/30/30) ─────────────
            await page.GotoAsync($"{BaseUrl}/Settings/MealPlanning");
            await page.WaitForURLAsync("**/Settings/MealPlanning**");

            var defaultsForm = page.Locator("#meal-planning-defaults-form");
            await Assertions.Expect(defaultsForm).ToBeVisibleAsync();
            var defaultSliders = defaultsForm.Locator("input[type='range']");
            await defaultSliders.Nth(0).FillAsync("40");
            await Assertions.Expect(defaultsForm.Locator("input[name='wasteWeight']")).ToHaveValueAsync("40");
            await Assertions.Expect(defaultsForm.Locator("input[name='costWeight']")).ToHaveValueAsync("30");
            await Assertions.Expect(defaultsForm.Locator("input[name='varietyWeight']")).ToHaveValueAsync("30");

            await page.RunAndWaitForResponseAsync(
                () => defaultsForm.Locator("button[type='submit']").ClickAsync(),
                r => r.Url.Contains("handler=SetMealPlanningDefaults") && r.Status == 200);

            await Assertions.Expect(defaultsForm.Locator("input[name='wasteWeight']")).ToHaveValueAsync("40");
            await Assertions.Expect(defaultsForm.Locator("input[name='costWeight']")).ToHaveValueAsync("30");
            await Assertions.Expect(defaultsForm.Locator("input[name='varietyWeight']")).ToHaveValueAsync("30");

            // ── 3. Navigate to Meal Plan and verify household fallback ──────────────
            await page.GotoAsync($"{BaseUrl}/MealPlan");
            await page.WaitForURLAsync("**/MealPlan**");
            await Assertions.Expect(page.Locator(".wkgrid")).ToBeVisibleAsync();

            // The budget chip should be present on the plan bar
            await Assertions.Expect(page.Locator("#plan-cost-chip")).ToBeVisibleAsync();

            // ── 4. Verify no-override fallback, then create a week override ────────
            // The af-caret button toggles tuneOpen, revealing .tune-pop.
            var caretButton = page.Locator("button.af-caret[aria-label='Tune auto-fill']");
            await Assertions.Expect(caretButton).ToBeVisibleAsync();
            await caretButton.ClickAsync();

            // Wait for the tune popover to appear (x-show="tuneOpen")
            var tunePop = page.Locator(".tune-pop");
            await Assertions.Expect(tunePop).ToBeVisibleAsync();

            await Assertions.Expect(page.Locator("#plan-bar-autofill"))
                .ToHaveAttributeAsync("data-plan-tune-override", "false");
            await AssertWeightsAsync(tunePop, "40", "30", "30");

            // Move Waste to 50 WITHOUT clicking Generate. The other two buckets rebalance to 25/25.
            var wasteSlider = tunePop.Locator("input[type='range']").Nth(0);
            await page.RunAndWaitForResponseAsync(
                async () =>
                {
                    await wasteSlider.FillAsync("50");
                    await wasteSlider.PressAsync("Tab");
                },
                r => r.Url.Contains("handler=SetPlanningSettings") && r.Status == 200);

            // The OOB response must replace the component with the server-resolved override.
            await Assertions.Expect(page.Locator("#plan-bar-autofill"))
                .ToHaveAttributeAsync("data-plan-tune-override", "true");
            await AssertWeightsAsync(page.Locator("#plan-bar-autofill .tune-pop"), "50", "25", "25");

            // Reopen after the OOB replacement and verify the visible override explanation.
            await page.Locator("button.af-caret[aria-label='Tune auto-fill']").ClickAsync();
            await Assertions.Expect(page.Locator("#plan-bar-autofill .tune-pop")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#plan-bar-autofill .tune-pop"))
                .ToContainTextAsync("Using priorities saved for this week");
            await AssertWeightsAsync(page.Locator("#plan-bar-autofill .tune-pop"), "50", "25", "25");

            // Keep the existing budget journey in this same end-to-end regression: changing the
            // weekly budget also persists through the same OOB path.
            var budgetInput = page.Locator(".tune-budget input[type='number']");
            await Assertions.Expect(budgetInput).ToBeVisibleAsync();
            await budgetInput.FillAsync("50");

            // Trigger @change by tabbing away — this fires persistSettings() which posts
            // ?handler=SetPlanningSettings. htmx processes the OOB response updating the bar.
            await page.RunAndWaitForResponseAsync(
                async () => await budgetInput.PressAsync("Tab"),
                r => r.Url.Contains("handler=SetPlanningSettings") && r.Status == 200);

            // ── 5. Assert #plan-cost-chip and the week override survive settings OOB ─
            await Assertions.Expect(page.Locator("#plan-cost-chip")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#plan-bar-autofill"))
                .ToHaveAttributeAsync("data-plan-tune-override", "true");
            await AssertWeightsAsync(page.Locator("#plan-bar-autofill .tune-pop"), "50", "25", "25");

            // ── 6. Cell op: assign a note to the first empty cell ────────────────
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
            await Assertions.Expect(page.Locator("#plan-bar-autofill"))
                .ToHaveAttributeAsync("data-plan-tune-override", "true");
            await AssertWeightsAsync(page.Locator("#plan-bar-autofill .tune-pop"), "50", "25", "25");

            // ── 7. Adjacent week falls back to household defaults ─────────────────
            var nextWeekButton = page.Locator("button.wknav-btn[aria-label='Next week']");
            await page.RunAndWaitForResponseAsync(
                () => nextWeekButton.ClickAsync(),
                r => r.Url.Contains("handler=Grid") && r.Status == 200);

            await Assertions.Expect(page.Locator("#plan-bar-autofill"))
                .ToHaveAttributeAsync("data-plan-tune-override", "false");
            await AssertWeightsAsync(page.Locator("#plan-bar-autofill .tune-pop"), "40", "30", "30");

            var previousWeekButton = page.Locator("button.wknav-btn[aria-label='Previous week']");
            await page.RunAndWaitForResponseAsync(
                () => previousWeekButton.ClickAsync(),
                r => r.Url.Contains("handler=Grid") && r.Status == 200);

            await Assertions.Expect(page.Locator("#plan-bar-autofill"))
                .ToHaveAttributeAsync("data-plan-tune-override", "true");
            await AssertWeightsAsync(page.Locator("#plan-bar-autofill .tune-pop"), "50", "25", "25");

            // ── 8. Navigate away (to Recipes) and back ─────────────────────────────
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

            // Open popover and confirm both persisted budget and week override weights reflect
            // server truth after the hard reload.
            var caretButton2 = page.Locator("button.af-caret[aria-label='Tune auto-fill']");
            await caretButton2.ClickAsync();
            var tunePop2 = page.Locator(".tune-pop");
            await Assertions.Expect(tunePop2).ToBeVisibleAsync();

            await Assertions.Expect(page.Locator("#plan-bar-autofill"))
                .ToHaveAttributeAsync("data-plan-tune-override", "true");
            await Assertions.Expect(tunePop2).ToContainTextAsync("Using priorities saved for this week");
            await AssertWeightsAsync(tunePop2, "50", "25", "25");

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

    private static async Task AssertWeightsAsync(ILocator popover, string waste, string cost, string variety)
    {
        var sliders = popover.Locator("input[type='range']");
        await Assertions.Expect(sliders.Nth(0)).ToHaveValueAsync(waste);
        await Assertions.Expect(sliders.Nth(1)).ToHaveValueAsync(cost);
        await Assertions.Expect(sliders.Nth(2)).ToHaveValueAsync(variety);
    }
}
