using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E tests (Playwright) for the §7e Stores &amp; Deals journey (P5-2 / DJ1):
///   1. Search the (stubbed) store directory by postal code, subscribe → the store appears active in a
///      "not pulled yet" state.
///   2. Pause a subscription → it shows as paused (and can be resumed).
///   3. Unsubscribe a subscription → it drops from the active list.
///   4. RLS — a second household sees none of the first household's subscriptions.
///
/// Uses the canned StubFlyerSourceAdapter (deterministic directory), so no live Flipp call is made.
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class StoresAndDealsJourneyTests(AppHostFixture appHost) : IAsyncLifetime
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

    [Fact(DisplayName = "Search the directory, subscribe → the store appears active in 'not pulled yet' state")]
    public async Task Search_And_Subscribe_Shows_Active_Store()
    {
        await using var context = await NewContextAsync();
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });
        try
        {
            var page = await NewStoresPageAsync(context, "e2e-deals-sub");

            await SubscribeToAsync(page, "FreshCo");

            // Active subscription row present, not paused, in a "not pulled yet" state.
            var freshco = page.Locator(".store-row[data-store-name='FreshCo']:not(.store-row--paused)");
            await Assertions.Expect(freshco).ToHaveCountAsync(1);
            await Assertions.Expect(freshco).ToContainTextAsync("Not pulled yet");
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-deals-subscribe.zip" });
        }
    }

    [Fact(DisplayName = "Pause a subscription → it shows as paused; resume restores it to active")]
    public async Task Pause_Then_Resume_Toggles_State()
    {
        await using var context = await NewContextAsync();
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });
        try
        {
            var page = await NewStoresPageAsync(context, "e2e-deals-pause");
            await SubscribeToAsync(page, "Metro");

            // Pause.
            await page.Locator(".store-row[data-store-name='Metro'] button:has-text('Pause')").ClickAsync();
            var paused = page.Locator(".store-row--paused[data-store-name='Metro']");
            await Assertions.Expect(paused).ToHaveCountAsync(1);
            await Assertions.Expect(paused).ToContainTextAsync("Paused");

            // Resume.
            await paused.Locator("button:has-text('Resume')").ClickAsync();
            await Assertions.Expect(
                page.Locator(".store-row[data-store-name='Metro']:not(.store-row--paused)")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".store-row--paused[data-store-name='Metro']")).ToHaveCountAsync(0);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-deals-pause.zip" });
        }
    }

    [Fact(DisplayName = "Unsubscribe a subscription → it drops from the active list")]
    public async Task Unsubscribe_Drops_From_Active()
    {
        await using var context = await NewContextAsync();
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });
        try
        {
            var page = await NewStoresPageAsync(context, "e2e-deals-unsub");
            await SubscribeToAsync(page, "Sobeys");

            await Assertions.Expect(
                page.Locator(".store-row[data-store-name='Sobeys']:not(.store-row--paused)")).ToHaveCountAsync(1);

            await page.Locator(".store-row[data-store-name='Sobeys'] button:has-text('Unsubscribe')").ClickAsync();

            // No longer in the active list.
            await Assertions.Expect(
                page.Locator(".store-row[data-store-name='Sobeys']:not(.store-row--paused)")).ToHaveCountAsync(0);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-deals-unsub.zip" });
        }
    }

    [Fact(DisplayName = "RLS: a second household sees none of the first household's subscriptions")]
    public async Task Second_Household_Sees_No_Subscriptions()
    {
        // Household A subscribes.
        await using (var contextA = await NewContextAsync())
        {
            var pageA = await NewStoresPageAsync(contextA, "e2e-deals-rls-a");
            await SubscribeToAsync(pageA, "Walmart");
            await Assertions.Expect(
                pageA.Locator(".store-row[data-store-name='Walmart']")).ToHaveCountAsync(1);
        }

        // A brand-new household B sees the empty state — none of A's stores.
        await using var contextB = await NewContextAsync();
        var pageB = await NewStoresPageAsync(contextB, "e2e-deals-rls-b");
        await Assertions.Expect(pageB.Locator(".store-row")).ToHaveCountAsync(0);
        await Assertions.Expect(pageB.Locator("text=No stores yet")).ToBeVisibleAsync();
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private Task<IBrowserContext> NewContextAsync() =>
        _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });

    private async Task<IPage> NewStoresPageAsync(IBrowserContext context, string emailPrefix)
    {
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

        var email = $"{emailPrefix}-{Guid.NewGuid():N}@test.local";
        await page.GotoAsync($"{BaseUrl}/Account/Register");
        await page.WaitForURLAsync("**/Account/Register");
        await page.FillAsync("[name='Input.HouseholdName']", "Deals Journey Household");
        await page.FillAsync("[name='Input.Email']", email);
        await page.FillAsync("[name='Input.DisplayName']", "Deals User");
        await page.FillAsync("[name='Input.Password']", "testpass1");
        await page.ClickAsync("button[type=submit]");
        await page.WaitForURLAsync("**/Today**");

        await page.GotoAsync($"{BaseUrl}/Settings/StoresAndDeals");
        await page.WaitForURLAsync("**/Settings/StoresAndDeals**");
        return page;
    }

    private static async Task SubscribeToAsync(IPage page, string merchant)
    {
        await page.FillAsync(".store-search__postal", "K1A0B1");
        await page.ClickAsync(".store-search button[type=submit]");

        var subscribeButton = page.Locator($".store-result[data-merchant='{merchant}'] button:has-text('Subscribe')");
        await Assertions.Expect(subscribeButton).ToBeVisibleAsync();
        await subscribeButton.ClickAsync();

        await Assertions.Expect(page.Locator($".store-row[data-store-name='{merchant}']")).ToHaveCountAsync(1);
    }
}
