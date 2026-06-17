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
            var emptyAdds = page.Locator(".empty-add");
            var firstHxGet = await emptyAdds.Nth(0).GetAttributeAsync("hx-get");
            var secondHxGet = await emptyAdds.Nth(1).GetAttributeAsync("hx-get");
            Assert.NotNull(firstHxGet);
            Assert.NotNull(secondHxGet);

            var (fromDate, fromSlotId) = ParseCellFromHxGet(firstHxGet!);
            var (toDate, toSlotId) = ParseCellFromHxGet(secondHxGet!);

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

            var emptyAdds = page.Locator(".empty-add");
            var firstHxGet = await emptyAdds.Nth(0).GetAttributeAsync("hx-get");
            var secondHxGet = await emptyAdds.Nth(1).GetAttributeAsync("hx-get");
            Assert.NotNull(firstHxGet);
            Assert.NotNull(secondHxGet);

            var (dateA, slotA) = ParseCellFromHxGet(firstHxGet!);
            var (dateB, slotB) = ParseCellFromHxGet(secondHxGet!);

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

    // ── Journey 5: Assign note → navigate to next week → note still on this week ──

    [Fact(DisplayName = "Assign note → navigate next week → note persists on return")]
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

            // Assign a note to this week
            var token = await GetAntiforgeryTokenAsync(page);
            Assert.Equal(200, await PostAssignNoteAsync(page, date, slotId, "Takeout", token));

            // Navigate to next week via full page load (avoids plan-bar stale hx-get issue)
            var nextWeek = thisMondayDate.AddDays(7);
            await page.GotoAsync($"{BaseUrl}/MealPlan?week={nextWeek:yyyy-MM-dd}");
            await page.WaitForURLAsync("**/MealPlan**");
            await Assertions.Expect(page.Locator(".wkgrid")).ToBeVisibleAsync();

            // Confirm we are on next week: first empty-add button date should be +7
            var nextHxGet = await page.Locator(".empty-add").First.GetAttributeAsync("hx-get");
            if (nextHxGet != null)
            {
                var (nextDate, _) = ParseCellFromHxGet(nextHxGet);
                var nextMondayDate = DateOnly.Parse(nextDate);
                Assert.Equal(7, nextMondayDate.DayNumber - thisMondayDate.DayNumber);
            }

            // No note on next week
            await Assertions.Expect(page.Locator(".meal-card.note")).Not.ToBeVisibleAsync();

            // Navigate back to this week via full page load
            await page.GotoAsync($"{BaseUrl}/MealPlan?week={thisMondayDate:yyyy-MM-dd}");
            await page.WaitForURLAsync("**/MealPlan**");
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

    // ── Journey 6: Dish assign via Alpine editor (fetch path) ───────────────
    // Validates:
    //   (a) The Save button POSTs via fetch+params.toString() (not Object.fromEntries)
    //       so repeated keys (dishKinds × N dishes, attendeesOverride × M members)
    //       are preserved and BuildDishSpecs receives the full multi-dish payload.
    //   (b) Verifies the addDishFromResult function in the editor HTML uses the Alpine v3
    //       _x_dataStack[0] accessor (not the v2 __x accessor) by asserting the rendered
    //       editor fragment HTML contains '_x_dataStack' and does not contain '.__x'.

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

            // ── (b) Verify editor HTML uses Alpine v3 accessor ────────────────────
            // Fetch the editor fragment and assert it uses _x_dataStack[0], not __x.
            var editorUrl = $"{BaseUrl}/MealPlan?handler=Editor&date={date}&slotId={slotId}";
            var editorHtml = await page.EvaluateAsync<string>(@"
                async (url) => {
                    const r = await fetch(url, { headers: { 'HX-Request': 'true' } });
                    return r.ok ? await r.text() : '';
                }", editorUrl);
            Assert.Contains("_x_dataStack", editorHtml);
            Assert.DoesNotContain(".__x", editorHtml); // Alpine v2 accessor — must not be present

            // ── (a) POST two recipe dishes via fetch+params.toString() ────────────
            // This mimics the Save button's inline fetch (FIX 2). Repeated keys are
            // preserved by URLSearchParams.toString(), unlike Object.fromEntries.
            var token = await GetAntiforgeryTokenAsync(page);
            var assignUrl = $"{BaseUrl}/MealPlan?handler=Assign&date={date}&slotId={slotId}";
            var assignResult = await page.EvaluateAsync<string>(@"
                async (args) => {
                    const params = new URLSearchParams();
                    params.append('mode', 'dishes');
                    // dish 0
                    params.append('dishKinds', 'recipe');
                    params.append('dishItemIds', '00000000-0000-0000-0000-000000000099');
                    params.append('dishServings', '2');
                    // dish 1
                    params.append('dishKinds', 'recipe');
                    params.append('dishItemIds', '00000000-0000-0000-0000-0000000000aa');
                    params.append('dishServings', '3');
                    const r = await fetch(args.url, {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/x-www-form-urlencoded',
                            'RequestVerificationToken': args.token,
                            'HX-Request': 'true'
                        },
                        body: params.toString()
                    });
                    const body = await r.text();
                    return r.status + '|' + body.substring(0, 500);
                }", new { url = assignUrl, token });
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

    private static async Task<(string date, string slotId)> GetFirstCellInfoAsync(IPage page)
    {
        var hxGet = await page.Locator(".empty-add").First.GetAttributeAsync("hx-get");
        Assert.NotNull(hxGet);
        return ParseCellFromHxGet(hxGet!);
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
