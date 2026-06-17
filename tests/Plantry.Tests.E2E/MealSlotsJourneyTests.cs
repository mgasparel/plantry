using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E tests (Playwright) for the Meal Slots settings journey (P3-1):
///   1. Login → navigate to /Settings → click through to /Settings/MealSlots.
///   2. Add a new slot.
///   3. Reorder slots with the up/down buttons.
///   4. Archive a slot.
///
/// Boots the full service graph via AppHostFixture.
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class MealSlotsJourneyTests(AppHostFixture appHost) : IAsyncLifetime
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

    [Fact(DisplayName = "Login → Settings hub → MealSlots page renders default seeded slots")]
    public async Task Navigate_To_MealSlots_Sees_Default_Slots()
    {
        var uniqueEmail = $"e2e-slots-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── Register household ─────────────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", "Slots Journey Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "Slots User");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── Navigate to Settings/MealSlots ─────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Settings/MealSlots");
            await page.WaitForURLAsync("**/Settings/MealSlots**");

            // ── Seeded slots are visible ───────────────────────────────────────
            await Assertions.Expect(page.Locator(".slot-card")).ToHaveCountAsync(3);
            var inputValues = await page.Locator(".slot-name-input").EvaluateAllAsync<string[]>(
                "inputs => inputs.map(i => i.value)");
            Assert.Contains("Breakfast", inputValues);
            Assert.Contains("Lunch", inputValues);
            Assert.Contains("Dinner", inputValues);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-slots-navigate.zip" });
        }
    }

    [Fact(DisplayName = "Add a new meal slot and verify it appears in the list")]
    public async Task Add_Slot_Appears_In_List()
    {
        var uniqueEmail = $"e2e-addslot-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            await RegisterAndGoToMealSlots(page, $"{BaseUrl}", uniqueEmail, password);

            // ── Add a new slot ─────────────────────────────────────────────────
            await page.FillAsync(".slot-add input", "Brunch");
            await page.ClickAsync(".slot-add-btn");

            // ── New slot appears ───────────────────────────────────────────────
            await Assertions.Expect(page.Locator(".slot-card")).ToHaveCountAsync(4);
            var inputValues = await page.Locator(".slot-name-input").EvaluateAllAsync<string[]>(
                "inputs => inputs.map(i => i.value)");
            Assert.Contains("Brunch", inputValues);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-slots-add.zip" });
        }
    }

    [Fact(DisplayName = "Reorder slots with up-arrow button and verify the new order")]
    public async Task Reorder_Slot_Changes_Order()
    {
        var uniqueEmail = $"e2e-reorder-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            await RegisterAndGoToMealSlots(page, $"{BaseUrl}", uniqueEmail, password);

            // Default order: Breakfast (0), Lunch (1), Dinner (2)
            // Click "Move up" on Lunch — second slot's up button
            var moveUpButtons = page.Locator(".slot-mini[title='Move up']");
            await moveUpButtons.Nth(1).ClickAsync(); // Lunch's move-up

            // Wait for htmx swap to settle
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            // After moving Lunch up: Lunch (0), Breakfast (1), Dinner (2)
            var inputValues = await page.Locator(".slot-name-input").EvaluateAllAsync<string[]>(
                "inputs => inputs.map(i => i.value)");
            Assert.Equal("Lunch", inputValues[0]);
            Assert.Equal("Breakfast", inputValues[1]);
            Assert.Equal("Dinner", inputValues[2]);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-slots-reorder.zip" });
        }
    }

    [Fact(DisplayName = "Set default attendees on a slot and verify the attendee label updates")]
    public async Task Set_Attendees_Updates_Label()
    {
        var uniqueEmail = $"e2e-attendees-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            await RegisterAndGoToMealSlots(page, $"{BaseUrl}", uniqueEmail, password);

            // Initially all slots have "No one yet" as the attendee label
            var firstAttLabel = page.Locator(".slot-card").First.Locator(".att-label");
            await Assertions.Expect(firstAttLabel).ToContainTextAsync("No one yet");

            // Open the attendee popover on the first slot (Breakfast)
            await page.Locator(".slot-card").First.Locator(".att-trigger").ClickAsync();

            // The popover should open; click the first member option (toggles Alpine state locally —
            // no POST fires yet; the POST is deferred until the popover closes)
            var firstOpt = page.Locator(".att-pop").First.Locator(".att-opt").First;
            await Assertions.Expect(firstOpt).ToBeVisibleAsync();
            await firstOpt.ClickAsync();

            // Close the popover by clicking outside — Alpine's close() fires the deferred POST
            await page.RunAndWaitForResponseAsync(
                async () => await page.Locator("h1").ClickAsync(),
                r => r.Url.Contains("handler=Attendees") && r.Status == 200);

            // After the htmx swap, the label should no longer be "No one yet"
            var updatedAttLabel = page.Locator(".slot-card").First.Locator(".att-label");
            await Assertions.Expect(updatedAttLabel).Not.ToContainTextAsync("No one yet");
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-slots-attendees.zip" });
        }
    }

    [Fact(DisplayName = "Archive a slot and verify it disappears from the active list")]
    public async Task Archive_Slot_Removes_From_List()
    {
        var uniqueEmail = $"e2e-archive-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            await RegisterAndGoToMealSlots(page, $"{BaseUrl}", uniqueEmail, password);

            // ── Click archive on the first slot (Breakfast) ────────────────────
            await page.Locator(".slot-del").First.ClickAsync();

            // ── One less slot in the active list ───────────────────────────────
            await Assertions.Expect(page.Locator(".slot-card")).ToHaveCountAsync(2);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-slots-archive.zip" });
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static async Task RegisterAndGoToMealSlots(
        IPage page,
        string baseUrl,
        string email,
        string password)
    {
        await page.GotoAsync($"{baseUrl}/Account/Register");
        await page.WaitForURLAsync("**/Account/Register");
        await page.FillAsync("[name='Input.HouseholdName']", "Journey Household");
        await page.FillAsync("[name='Input.Email']", email);
        await page.FillAsync("[name='Input.DisplayName']", "Journey User");
        await page.FillAsync("[name='Input.Password']", password);
        await page.ClickAsync("button[type=submit]");
        await page.WaitForURLAsync("**/Today**");

        await page.GotoAsync($"{baseUrl}/Settings/MealSlots");
        await page.WaitForURLAsync("**/Settings/MealSlots**");
    }
}
