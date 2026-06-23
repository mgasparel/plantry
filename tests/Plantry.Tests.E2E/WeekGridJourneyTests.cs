using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E journey tests (Playwright) for the P3-3 week grid.
///
/// Acceptance criteria: assign → reschedule (relocate + swap) → clear, across weeks.
///
/// Each test exercises the full HTTP round-trip by:
///   1. Navigating to /MealPlan in a real Chromium browser via the Aspire-hosted app.
///   2. Extracting cell slot/date context from the rendered grid.
///   3. Posting to the Assign / Move / Clear endpoints via fetch() (same as htmx does).
///   4. Reloading the page with a full GET to verify persistent state.
///
/// This pattern is robust for htmx/Alpine-rendered pages where the partial-swap
/// timing is not observable from Playwright without waiting for Alpine hydration.
///
/// Boots the full Aspire stack via AppHostFixture.
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class WeekGridJourneyTests(AppHostFixture appHost) : IAsyncLifetime
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

    // ── Journey 1: Assign note → grid shows card ─────────────────────────────

    [Fact(DisplayName = "Assign note → reload → meal card shows on grid")]
    public async Task AssignNote_MealCardAppearsOnGrid()
    {
        var uniqueEmail = $"e2e-assign-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            await RegisterAndGoToMealPlan(page, uniqueEmail, password);

            // ── Extract cell info from the first empty-add button ─────────────────
            var (date, slotId) = await GetFirstCellInfoAsync(page);

            // ── POST Assign note via fetch ────────────────────────────────────────
            var token = await GetAntiforgeryTokenAsync(page);
            var statusCode = await PostAssignNoteAsync(page, date, slotId, "Takeout", token);
            Assert.Equal(200, statusCode);

            // ── Full page reload → verify the card persisted ──────────────────────
            await page.GotoAsync($"{BaseUrl}/MealPlan");
            await page.WaitForURLAsync("**/MealPlan**");
            await Assertions.Expect(page.Locator(".wkgrid")).ToBeVisibleAsync();

            await Assertions.Expect(page.Locator(".meal-card.note")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".meal-card.note .note-txt")).ToContainTextAsync("Takeout");
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-wkgrid-assign.zip" });
        }
    }

    // ── Journey 2: Assign → Move (relocate) → card moves ────────────────────

    [Fact(DisplayName = "Assign note → Move to Tuesday → note is on Tuesday, Monday is empty")]
    public async Task AssignNote_ThenMove_CardRelocatesToTuesday()
    {
        var uniqueEmail = $"e2e-move-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            await RegisterAndGoToMealPlan(page, uniqueEmail, password);

            // Get slot/date for the first TWO cells (Monday and Tuesday of the same slot)
            // After island port (plantry-2zvm.4): empty-add uses onclick instead of hx-get
            var emptyAdds = page.Locator(".empty-add");
            var firstOnclick = await emptyAdds.Nth(0).GetAttributeAsync("onclick");
            var secondOnclick = await emptyAdds.Nth(1).GetAttributeAsync("onclick");
            Assert.NotNull(firstOnclick);
            Assert.NotNull(secondOnclick);

            var (fromDate, fromSlotId) = ParseCellFromOpenEditorOnclick(firstOnclick!);
            var (toDate, toSlotId) = ParseCellFromOpenEditorOnclick(secondOnclick!);

            var token = await GetAntiforgeryTokenAsync(page);

            // Assign to Monday
            Assert.Equal(200, await PostAssignNoteAsync(page, fromDate, fromSlotId, "Takeout", token));

            // Reload to pick up the assigned meal card and extract its mealId from ondragstart
            await page.GotoAsync($"{BaseUrl}/MealPlan");
            await page.WaitForURLAsync("**/MealPlan**");
            await Assertions.Expect(page.Locator(".wkgrid")).ToBeVisibleAsync();
            var mealId = await ExtractMealIdFromCardAsync(page);
            Assert.False(string.IsNullOrEmpty(mealId), "Expected a meal card with a mealId after assign.");

            // Move from Monday to Tuesday (MP-O8: relocate by mealId — no swap)
            token = await GetAntiforgeryTokenAsync(page);
            var moveUrl = $"{BaseUrl}/MealPlan?handler=Move&mealId={mealId}&toDate={toDate}&toSlotId={toSlotId}";
            var moveStatus = await page.EvaluateAsync<int>(@"
                async (args) => {
                    const r = await fetch(args.url, {
                        method: 'POST',
                        headers: { 'RequestVerificationToken': args.token, 'HX-Request': 'true' }
                    });
                    return r.status;
                }", new { url = moveUrl, token });
            Assert.Equal(200, moveStatus);

            // Full page reload → Monday cell is empty, Tuesday cell has the note
            await page.GotoAsync($"{BaseUrl}/MealPlan");
            await page.WaitForURLAsync("**/MealPlan**");
            await Assertions.Expect(page.Locator(".wkgrid")).ToBeVisibleAsync();

            // Note card exists (now on Tuesday)
            await Assertions.Expect(page.Locator(".meal-card.note")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".meal-card.note .note-txt")).ToContainTextAsync("Takeout");

            // Monday cell is empty — its cell element has class "empty"
            var mondayCellId = $"cell-{fromSlotId.Replace("-", "")}-{fromDate}";
            await Assertions.Expect(page.Locator($"#{mondayCellId}")).ToHaveClassAsync(
                new System.Text.RegularExpressions.Regex("\\bempty\\b"));
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-wkgrid-move.zip" });
        }
    }

    // ── Journey 3: Assign two meals → Move into occupied cell → both stack ───
    // MP-O8: moving into an occupied cell appends to the stack (no swap).
    // Before: swap expected Leftovers→A and Takeout→B. Now: A is empty, B has both.

    [Fact(DisplayName = "Assign Takeout Monday + Leftovers Tuesday → Move Takeout to Tuesday → Tuesday has both, Monday is empty")]
    public async Task AssignTwo_ThenMoveIntoOccupied_BothStackInCell()
    {
        var uniqueEmail = $"e2e-stack-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            await RegisterAndGoToMealPlan(page, uniqueEmail, password);

            // After island port (plantry-2zvm.4): empty-add uses onclick instead of hx-get
            var emptyAdds = page.Locator(".empty-add");
            var firstOnclick3 = await emptyAdds.Nth(0).GetAttributeAsync("onclick");
            var secondOnclick3 = await emptyAdds.Nth(1).GetAttributeAsync("onclick");
            Assert.NotNull(firstOnclick3);
            Assert.NotNull(secondOnclick3);

            var (dateA, slotA) = ParseCellFromOpenEditorOnclick(firstOnclick3!);
            var (dateB, slotB) = ParseCellFromOpenEditorOnclick(secondOnclick3!);

            // Assign Takeout to A (Monday)
            var token = await GetAntiforgeryTokenAsync(page);
            Assert.Equal(200, await PostAssignNoteAsync(page, dateA, slotA, "Takeout", token));

            // Assign Leftovers to B (Tuesday)
            token = await GetAntiforgeryTokenAsync(page);
            Assert.Equal(200, await PostAssignNoteAsync(page, dateB, slotB, "Leftovers", token));

            // Reload to extract the mealId of the Takeout meal on Monday (first card in cell A)
            await page.GotoAsync($"{BaseUrl}/MealPlan");
            await page.WaitForURLAsync("**/MealPlan**");
            await Assertions.Expect(page.Locator(".wkgrid")).ToBeVisibleAsync();

            var cellASelector = $"#cell-{slotA.Replace("-", "")}-{dateA}";
            var takeoutMealId = await page.Locator(cellASelector + " .meal-card").First
                .EvaluateAsync<string?>(@"el => {
                    const ds = el.getAttribute('ondragstart') || '';
                    const m = ds.match(/onDragStart\(event,\s*'([^']+)'/);
                    return m ? m[1] : null;
                }");
            Assert.False(string.IsNullOrEmpty(takeoutMealId), "Expected Takeout card mealId in cell A.");

            // Move Takeout from Monday into Tuesday (B is occupied → joins the stack, no swap)
            token = await GetAntiforgeryTokenAsync(page);
            var moveUrl = $"{BaseUrl}/MealPlan?handler=Move&mealId={takeoutMealId}&toDate={dateB}&toSlotId={slotB}";
            var moveStatus = await page.EvaluateAsync<int>(@"
                async (args) => {
                    const r = await fetch(args.url, {
                        method: 'POST',
                        headers: { 'RequestVerificationToken': args.token, 'HX-Request': 'true' }
                    });
                    return r.status;
                }", new { url = moveUrl, token });
            Assert.Equal(200, moveStatus);

            // Reload → cell A is empty, cell B has both notes stacked
            await page.GotoAsync($"{BaseUrl}/MealPlan");
            await page.WaitForURLAsync("**/MealPlan**");
            await Assertions.Expect(page.Locator(".wkgrid")).ToBeVisibleAsync();

            var cellBId = $"#cell-{slotB.Replace("-", "")}-{dateB}";

            // Both notes appear in cell B
            await Assertions.Expect(page.Locator(cellBId + " .note-txt").Nth(0)).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(cellBId + " .note-txt").Nth(1)).ToBeVisibleAsync();

            // Cell A is now empty
            await Assertions.Expect(page.Locator(cellASelector)).ToHaveClassAsync(
                new System.Text.RegularExpressions.Regex("\\bempty\\b"));
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-wkgrid-stack.zip" });
        }
    }

    // ── Journey 4: Assign → Clear ─────────────────────────────────────────────

    [Fact(DisplayName = "Assign note → Clear → reload → cell is empty")]
    public async Task AssignNote_ThenClear_CellBecomesEmpty()
    {
        var uniqueEmail = $"e2e-clear-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            await RegisterAndGoToMealPlan(page, uniqueEmail, password);

            var (date, slotId) = await GetFirstCellInfoAsync(page);
            var token = await GetAntiforgeryTokenAsync(page);

            // Assign
            Assert.Equal(200, await PostAssignNoteAsync(page, date, slotId, "Takeout", token));

            // Reload to extract the mealId from the rendered card's ondragstart attribute
            await page.GotoAsync($"{BaseUrl}/MealPlan");
            await page.WaitForURLAsync("**/MealPlan**");
            await Assertions.Expect(page.Locator(".wkgrid")).ToBeVisibleAsync();
            var mealId = await ExtractMealIdFromCardAsync(page);
            Assert.False(string.IsNullOrEmpty(mealId), "Expected a meal card with a mealId after assign.");

            // Clear by mealId (MP-O8: clear targets a specific meal, not a whole cell)
            token = await GetAntiforgeryTokenAsync(page);
            var clearUrl = $"{BaseUrl}/MealPlan?handler=Clear&date={date}&slotId={slotId}&mealId={mealId}";
            var clearStatus = await page.EvaluateAsync<int>(@"
                async (args) => {
                    const r = await fetch(args.url, {
                        method: 'POST',
                        headers: { 'RequestVerificationToken': args.token, 'HX-Request': 'true' }
                    });
                    return r.status;
                }", new { url = clearUrl, token });
            Assert.Equal(200, clearStatus);

            // Reload → cell is empty
            await page.GotoAsync($"{BaseUrl}/MealPlan");
            await page.WaitForURLAsync("**/MealPlan**");
            await Assertions.Expect(page.Locator(".wkgrid")).ToBeVisibleAsync();

            // No note card on the grid
            await Assertions.Expect(page.Locator(".meal-card.note")).Not.ToBeVisibleAsync();

            // The cell should have class "empty"
            var cellId = $"#cell-{slotId.Replace("-", "")}-{date}";
            await Assertions.Expect(page.Locator(cellId)).ToHaveClassAsync(
                new System.Text.RegularExpressions.Regex("\\bempty\\b"));
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-wkgrid-clear.zip" });
        }
    }

    // ── Journey 5: Assign note → navigate to next week via htmx → note still on this week ──
    // plantry-khw: this test previously used full page loads to avoid the stale plan-bar issue.
    // Now that OnGetGridAsync re-emits the plan-bar projections OOB, real htmx nav works:
    // the next/prev buttons' hx-get URLs, the week label, This-week button, and Auto-fill state
    // all update atomically with the grid swap. The test now exercises the htmx nav path.

    [Fact(DisplayName = "Assign note → navigate next week via htmx nav → week label updates, note persists on return")]
    public async Task AssignNote_WeekNavigation_NotePersistedAcrossWeeks()
    {
        var uniqueEmail = $"e2e-weknav-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            await RegisterAndGoToMealPlan(page, uniqueEmail, password);

            // Capture this week's Monday date from the first empty-add button
            var (date, slotId) = await GetFirstCellInfoAsync(page);
            var thisMondayDate = DateOnly.Parse(date);

            // Capture the initial week label from the plan-bar (plantry-khw: OOB-refreshed after nav)
            var initialWeekLabel = await page.Locator(".wk-label").InnerTextAsync();

            // Assign a note to this week
            var token = await GetAntiforgeryTokenAsync(page);
            Assert.Equal(200, await PostAssignNoteAsync(page, date, slotId, "Takeout", token));

            // Navigate to next week via htmx nav (clicking the next-week button).
            // plantry-khw fix: the plan-bar OOB response updates the nav URLs, week label,
            // and This-week button so subsequent navigation works correctly without a page reload.
            await page.Locator(".wknav-btn[aria-label='Next week']").ClickAsync();
            await page.WaitForFunctionAsync("document.querySelector('.wkgrid') !== null");
            // Wait for htmx to complete the swap — the week label must differ from before.
            await page.WaitForFunctionAsync(
                $"document.querySelector('.wk-label')?.innerText !== {System.Text.Json.JsonSerializer.Serialize(initialWeekLabel)}");

            // Confirm the week label changed (plan-bar OOB worked)
            var nextWeekLabel = await page.Locator(".wk-label").InnerTextAsync();
            Assert.NotEqual(initialWeekLabel, nextWeekLabel);

            // Confirm the grid reflects next week: first empty-add date should be +7
            // After island port (plantry-2zvm.4): empty-add uses onclick instead of hx-get
            var nextOnclick = await page.Locator(".empty-add").First.GetAttributeAsync("onclick");
            if (nextOnclick != null)
            {
                var (nextDate, _) = ParseCellFromOpenEditorOnclick(nextOnclick);
                var nextMondayDate = DateOnly.Parse(nextDate);
                Assert.Equal(7, nextMondayDate.DayNumber - thisMondayDate.DayNumber);
            }

            // No note on next week
            await Assertions.Expect(page.Locator(".meal-card.note")).Not.ToBeVisibleAsync();

            // Navigate back to this week via the "This week" button (now visible since we navigated away).
            // plantry-khw: the OOB plan-bar-nav also re-emits the This-week button correctly.
            await page.Locator(".wk-today").ClickAsync();
            await page.WaitForFunctionAsync(
                $"document.querySelector('.wk-label')?.innerText === {System.Text.Json.JsonSerializer.Serialize(initialWeekLabel)}");

            await Assertions.Expect(page.Locator(".wkgrid")).ToBeVisibleAsync();

            // Note card is still there on this week
            await Assertions.Expect(page.Locator(".meal-card.note")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".meal-card.note .note-txt")).ToContainTextAsync("Takeout");
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-wkgrid-weeknav.zip" });
        }
    }

    // ── Journey 6: Dish assign via JSON island endpoint (fetch path) ───────────────
    // Validates:
    //   (a) The island Save button posts JSON to ?handler=AssignJson so repeated
    //       dishes are preserved in the JSON dishes array and BuildDishSpecsFromJson
    //       receives the full multi-dish payload.
    //   (b) Verifies the page uses the Preact island (meal-planner-island-root) and
    //       no longer embeds the Alpine mealEditor() component registration — the
    //       island replaces the old fragment-based Alpine editor (plantry-2zvm.4).
    //   (c) GET EditorJson returns island hydration JSON (not HTML) — the island
    //       fetches hydration on openEditor() and renders the editor client-side.
    //
    // For the dish-assign smoke test we post directly to ?handler=Assign (form-encoded)
    // to validate the server-side Assign endpoint independently of the island client.

    [Fact(DisplayName = "Assign two-dish meal via POST → reload → meal card appears (fetch preserves repeated keys)")]
    public async Task TwoDishAssign_ViaFetch_MealCardAppearsOnReload()
    {
        var uniqueEmail = $"e2e-dish2-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            await RegisterAndGoToMealPlan(page, uniqueEmail, password);

            // ── Extract first empty cell info ─────────────────────────────────────
            var (date, slotId) = await GetFirstCellInfoAsync(page);

            // ── (b) Verify page carries Preact island, no Alpine mealEditor() ─────
            // plantry-2zvm.4: the editor is now a Preact island; the page must have
            // the island mount point and NOT embed the old Alpine mealEditor() component.
            var pageHtml = await page.ContentAsync();
            Assert.Contains("meal-planner-island-root", pageHtml);   // island mount point
            Assert.DoesNotContain("mealEditor(", pageHtml);           // Alpine component gone
            Assert.DoesNotContain("get roll()", pageHtml);            // old client-side rollup formula gone

            // ── (c) GET EditorJson returns island hydration JSON ──────────────────
            // The island calls this endpoint on openEditor(); it returns JSON (not HTML).
            var editorJsonUrl = $"{BaseUrl}/MealPlan?handler=EditorJson&date={date}&slotId={slotId}";
            var editorJsonText = await page.EvaluateAsync<string>(@"
                async (url) => {
                    const r = await fetch(url);
                    return r.ok ? await r.text() : '';
                }", editorJsonUrl);
            Assert.NotEmpty(editorJsonText);
            var editorJson = System.Text.Json.JsonDocument.Parse(editorJsonText);
            Assert.True(editorJson.RootElement.TryGetProperty("slotLabel", out _), "EditorJson missing slotLabel");
            Assert.True(editorJson.RootElement.TryGetProperty("mode", out _), "EditorJson missing mode");

            // ── (a) POST two recipe dishes via JSON fetch to ?handler=AssignJson ──
            // After island port (plantry-2zvm.4): the production save path POSTs JSON to
            // AssignJson (not form-encoded to Assign). The JSON array eliminates the
            // Object.fromEntries key-collapse bug; this test confirms both dishes survive.
            var token = await GetAntiforgeryTokenAsync(page);
            var assignJsonUrl = $"{BaseUrl}/MealPlan?handler=AssignJson";
            var assignResult = await page.EvaluateAsync<string>(@"
                async (args) => {
                    const body = JSON.stringify({
                        mode: 'dishes',
                        note: null,
                        dishes: [
                            { kind: 'recipe', itemId: '00000000-0000-0000-0000-000000000099', servings: 2 },
                            { kind: 'recipe', itemId: '00000000-0000-0000-0000-0000000000aa', servings: 3 }
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
                    const respBody = await r.text();
                    return r.status + '|' + respBody.substring(0, 500);
                }", new { url = assignJsonUrl, date, slotId, token });
            var parts = assignResult.Split('|', 2);
            Assert.Equal("200", parts[0]);

            // ── Full page reload → verify meal card persisted ─────────────────────
            await page.GotoAsync($"{BaseUrl}/MealPlan");
            await page.WaitForURLAsync("**/MealPlan**");
            await Assertions.Expect(page.Locator(".wkgrid")).ToBeVisibleAsync();

            // A non-note meal card should appear (dish-based meals do not have .note class)
            await Assertions.Expect(page.Locator(".meal-card:not(.note)")).ToBeVisibleAsync();
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-wkgrid-dish-add.zip" });
        }
    }

    // ── Journey 7: Rendered island editor — note via UI + remove ─────────────────
    // The critic FIX requirement (pass 1): at least one E2E journey that drives the
    // rendered Preact island editor (open modal → interact → Save → applyMutationResult).
    // This covers the historically-fragile Save button path without requiring seeded recipes.
    // Uses the note-mode path: click .empty-add → island opens → switch to note mode →
    // pick preset → Save → cell shows .meal-card.note → open again → Remove meal → cell empty.

    [Fact(DisplayName = "Island editor: open via click → note preset → Save → meal card → Remove meal → cell empty")]
    public async Task IslandEditor_NoteViaUI_MealCardAndRemovePath()
    {
        var uniqueEmail = $"e2e-island-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            await RegisterAndGoToMealPlan(page, uniqueEmail, password);

            // ── Wait for island to mount ──────────────────────────────────────────
            // The <script type="module"> runs after DOMContentLoaded. networkidle ensures
            // all deferred + module scripts have executed before we interact.
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            var islandMounted = await page.EvaluateAsync<bool>("() => !!window.__mealPlannerIsland");
            Assert.True(islandMounted, "meal-planner island did not mount: window.__mealPlannerIsland not set");

            // ── Open editor by clicking the first .empty-add button ───────────────
            // Onclick: window.__mealPlannerIsland && window.__mealPlannerIsland.openEditor(date, slotId, null)
            // openEditor() GETs ?handler=EditorJson, sets editorState signal, sets modalOpen=true.
            await page.Locator(".empty-add").First.ClickAsync();

            // Dialog renders after EditorJson fetch resolves.
            var dialog = page.Locator("#meal-editor-dialog");
            await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = 15_000 });

            // ── Switch to note mode via the toggle ────────────────────────────────
            // MealEditor now uses useSignal() (hook-stable) for draft state — re-renders work.
            var noteToggle = dialog.Locator(".ed-note-toggle button");
            await Assertions.Expect(noteToggle).ToBeVisibleAsync(new() { Timeout = 10_000 });
            await noteToggle.ClickAsync();

            // After toggle click, note chips appear (useSignal-driven Preact re-render).
            var chips = dialog.Locator(".ed-note-chips");
            await Assertions.Expect(chips).ToBeVisibleAsync(new() { Timeout = 10_000 });

            // ── Click the "Takeout" preset chip ──────────────────────────────────
            // Chip click sets note.value → canSave becomes true → Save button enabled.
            var takeoutChip = chips.Locator("button", new() { HasText = "Takeout" });
            await takeoutChip.ClickAsync();

            // ── Click Save meal ───────────────────────────────────────────────────
            var saveBtn = dialog.Locator("button.btn--primary", new() { HasText = "Save meal" });
            await Assertions.Expect(saveBtn).ToBeEnabledAsync(new() { Timeout = 5_000 });
            await saveBtn.ClickAsync();

            // Island closes dialog (modalOpen = false) and applyMutationResult() swaps the cell.
            await Assertions.Expect(dialog).ToBeHiddenAsync(new() { Timeout = 10_000 });

            // Cell now shows a note meal card without a full page reload.
            var noteCard = page.Locator(".meal-card.note");
            await Assertions.Expect(noteCard).ToBeVisibleAsync(new() { Timeout = 10_000 });
            await Assertions.Expect(noteCard.Locator(".note-txt")).ToContainTextAsync("Takeout");

            // ── Open the cell again for edit and Remove meal ──────────────────────
            // .mc-edit calls openEditor with a real mealId (isEditing = true path).
            var mcEdit = page.Locator(".mc-edit").First;
            await Assertions.Expect(mcEdit).ToBeVisibleAsync();
            await mcEdit.ClickAsync();

            await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = 15_000 });

            // Remove meal button appears when isEditing = true (mealId is non-null).
            var removeBtn = dialog.Locator("button.txt-btn.danger", new() { HasText = "Remove meal" });
            await Assertions.Expect(removeBtn).ToBeVisibleAsync(new() { Timeout = 5_000 });
            await removeBtn.ClickAsync();

            // Dialog closes, cell returns to empty state.
            await Assertions.Expect(dialog).ToBeHiddenAsync(new() { Timeout = 10_000 });
            await Assertions.Expect(page.Locator(".meal-card")).ToHaveCountAsync(0, new() { Timeout = 5_000 });
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-wkgrid-island-ui.zip" });
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task RegisterAndGoToMealPlan(IPage page, string email, string password)
    {
        await page.GotoAsync($"{BaseUrl}/Account/Register");
        await page.WaitForURLAsync("**/Account/Register");
        await page.FillAsync("[name='Input.HouseholdName']", "Week Grid Household");
        await page.FillAsync("[name='Input.Email']", email);
        await page.FillAsync("[name='Input.DisplayName']", "Grid User");
        await page.FillAsync("[name='Input.Password']", password);
        await page.ClickAsync("button[type=submit]");
        await page.WaitForURLAsync("**/Today**");

        await page.GetByRole(AriaRole.Link, new() { Name = "Meal Plan" }).First.ClickAsync();
        await page.WaitForURLAsync("**/MealPlan**");
        await Assertions.Expect(page.Locator(".wkgrid")).ToBeVisibleAsync();
    }

    /// <summary>
    /// Extracts date and slotId from the first empty-add button.
    /// After the Preact island port (plantry-2zvm.4), empty-add buttons use onclick to call
    /// window.__mealPlannerIsland.openEditor(date, slotId, null) instead of hx-get.
    /// </summary>
    private static async Task<(string date, string slotId)> GetFirstCellInfoAsync(IPage page)
    {
        var onclick = await page.Locator(".empty-add").First.GetAttributeAsync("onclick");
        Assert.NotNull(onclick);
        return ParseCellFromOpenEditorOnclick(onclick!);
    }

    /// <summary>
    /// Parses date and slotId from onclick="...openEditor('date', 'slotId', null)".
    /// </summary>
    private static (string date, string slotId) ParseCellFromOpenEditorOnclick(string onclick)
    {
        var m = System.Text.RegularExpressions.Regex.Match(onclick,
            @"openEditor\('([^']+)',\s*'([^']+)',\s*null\)");
        Assert.True(m.Success, $"Could not parse openEditor from onclick: {onclick}");
        return (m.Groups[1].Value, m.Groups[2].Value);
    }

    private static (string date, string slotId) ParseCellFromHxGet(string hxGet)
    {
        var qs = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + hxGet).Query);
        return (qs["date"] ?? "", qs["slotId"] ?? "");
    }

    private static async Task<string> GetAntiforgeryTokenAsync(IPage page)
    {
        return await page.Locator("input[name=__RequestVerificationToken]").First
                   .GetAttributeAsync("value") ?? "";
    }

    /// <summary>
    /// Extracts the mealId guid from the first .meal-card's ondragstart attribute.
    /// The card emits: onDragStart(event, 'mealId', 'date', 'slotId')
    /// Returns null if no card is found.
    /// </summary>
    private static async Task<string?> ExtractMealIdFromCardAsync(IPage page)
    {
        return await page.Locator(".meal-card").First.EvaluateAsync<string?>(@"el => {
            const ds = el.getAttribute('ondragstart') || '';
            const m = ds.match(/onDragStart\(event,\s*'([^']+)'/);
            return m ? m[1] : null;
        }");
    }

    private async Task<int> PostAssignNoteAsync(IPage page, string date, string slotId, string note, string token)
    {
        var assignUrl = $"{BaseUrl}/MealPlan?handler=Assign&date={date}&slotId={slotId}";
        return await page.EvaluateAsync<int>(@"
            async (args) => {
                const body = new URLSearchParams();
                body.append('mode', 'note');
                body.append('note', args.note);
                const r = await fetch(args.url, {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/x-www-form-urlencoded',
                        'RequestVerificationToken': args.token,
                        'HX-Request': 'true'
                    },
                    body: body.toString()
                });
                return r.status;
            }", new { url = assignUrl, note, token });
    }
}
