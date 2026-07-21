using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E journey tests (Playwright) for the MealPlan insights rail's breakpoint re-sync
/// (plantry-hldx, deferred from plantry-fxxh).
///
/// _WeekGrid.cshtml wires <c>railOpen</c> as Alpine local state that defaults from
/// <c>window.matchMedia('(max-width: 1000px)')</c> once at mount, then re-syncs on every
/// 'change' event fired by that MediaQueryList (registered in init(), torn down in destroy()).
/// The bug this guards against: without the change listener, railOpen is evaluated only once,
/// so a viewport that later CROSSES 1000px — notably an iPad rotating portrait&lt;-&gt;landscape —
/// would leave the rail hidden at desktop width, or overlaying the grid at mobile width, until
/// the next manual tap of the rail-collapse / rail-reopen buttons.
///
/// Uses real iPad viewport dimensions (1024x768 landscape, 768x1024 portrait — the 1000px
/// breakpoint sits between them) so "rotation" is exercised with real Chromium layout/matchMedia,
/// not a mock.
///
/// Boots the full Aspire stack via AppHostFixture.
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class MealPlanRailBreakpointE2ETests(AppHostFixture appHost) : IAsyncLifetime
{
    private const int LandscapeWidth = 1024;  // iPad landscape — above the 1000px breakpoint
    private const int LandscapeHeight = 768;
    private const int PortraitWidth = 768;    // iPad portrait — below the 1000px breakpoint
    private const int PortraitHeight = 1024;

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

    // ── Journey 1: rotation crossing the breakpoint re-syncs the rail both ways ──

    [Fact(DisplayName = "iPad rotation: landscape rail occupies layout → portrait collapses to overlay → landscape re-expands")]
    public async Task Rotation_CrossingBreakpoint_RailResyncsBothDirections()
    {
        var uniqueEmail = $"e2e-railrot-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = LandscapeWidth, Height = LandscapeHeight }
        });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            await RegisterAndGoToMealPlan(page, uniqueEmail, password);

            // ── Starts in landscape (>1000px): rail occupies the layout, not an overlay ───
            await AssertRailOpenAsync(page);
            await AssertRailNotOverlayAsync(page);

            // ── Rotate to portrait (<1000px): matchMedia 'change' fires on the crossing ───
            await page.SetViewportSizeAsync(PortraitWidth, PortraitHeight);
            await WaitForRailCollapsedAsync(page, true);

            // Below the breakpoint the rail becomes an overlay; the reopen affordance shows.
            await Assertions.Expect(page.Locator("#plan-rail-reopen")).ToBeVisibleAsync();

            // ── Rotate back to landscape (>1000px): rail re-expands without a manual tap ──
            await page.SetViewportSizeAsync(LandscapeWidth, LandscapeHeight);
            await WaitForRailCollapsedAsync(page, false);

            await Assertions.Expect(page.Locator("#plan-rail-reopen")).Not.ToBeVisibleAsync();
            await AssertRailNotOverlayAsync(page);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-rail-rotation.zip" });
        }
    }

    // ── Journey 2: manual toggle within a breakpoint is preserved (no false re-sync) ──

    [Fact(DisplayName = "Manual close on desktop → jitter width without crossing 1000px → rail stays manually closed")]
    public async Task ManualClose_JitterWithinBreakpoint_StaysClosed()
    {
        var uniqueEmail = $"e2e-railjit-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = LandscapeWidth, Height = LandscapeHeight }
        });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            await RegisterAndGoToMealPlan(page, uniqueEmail, password);
            await AssertRailOpenAsync(page);

            // Manually collapse the rail via the rail-collapse button (railOpen = false).
            await page.Locator(".rail-collapse").ClickAsync();
            await WaitForRailCollapsedAsync(page, true);

            // Jitter the viewport width within the SAME (>1000px) breakpoint — never crosses
            // 1000px, so matchMedia's 'change' listener must not fire and must not clobber the
            // user's manual choice.
            await page.SetViewportSizeAsync(1300, LandscapeHeight);
            await page.SetViewportSizeAsync(1100, LandscapeHeight);
            await page.SetViewportSizeAsync(LandscapeWidth, LandscapeHeight);

            // Give any (incorrect) async re-sync a moment to happen, then assert it did not.
            await page.WaitForTimeoutAsync(300);
            await AssertRailCollapsedNowAsync(page, true);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-rail-jitter.zip" });
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task RegisterAndGoToMealPlan(IPage page, string email, string password)
    {
        await page.GotoAsync($"{BaseUrl}/Account/Register");
        await page.WaitForURLAsync("**/Account/Register");
        await page.FillAsync("[name='Input.HouseholdName']", "Rail Breakpoint Household");
        await page.FillAsync("[name='Input.Email']", email);
        await page.FillAsync("[name='Input.DisplayName']", "Rail User");
        await page.FillAsync("[name='Input.Password']", password);
        await page.ClickAsync("button[type=submit]");
        await page.WaitForURLAsync("**/Today**");

        await page.GetByRole(AriaRole.Link, new() { Name = "Meal Plan" }).First.ClickAsync();
        await page.WaitForURLAsync("**/MealPlan**");
        await Assertions.Expect(page.Locator(".wkgrid")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("#plan-rail")).ToBeVisibleAsync();
    }

    private static async Task AssertRailOpenAsync(IPage page)
    {
        await Assertions.Expect(page.Locator("#plan-rail")).Not.ToHaveClassAsync(
            new System.Text.RegularExpressions.Regex("\\bcollapsed\\b"));
        await Assertions.Expect(page.Locator("#plan-rail-reopen")).Not.ToBeVisibleAsync();
    }

    /// <summary>
    /// Confirms the rail is laid out in-flow (desktop, >1000px) rather than the mobile overlay
    /// treatment, which switches it to position:absolute (plenish.css @media max-width:1000px).
    /// </summary>
    private static async Task AssertRailNotOverlayAsync(IPage page)
    {
        var position = await page.Locator("#plan-rail").EvaluateAsync<string>(
            "el => window.getComputedStyle(el).position");
        Assert.NotEqual("absolute", position);
    }

    /// <summary>
    /// Waits (polling, since the matchMedia 'change' listener updates Alpine state
    /// asynchronously relative to Playwright's viewport resize) for #plan-rail's collapsed
    /// state to reach the expected value.
    /// </summary>
    private static async Task WaitForRailCollapsedAsync(IPage page, bool expectedCollapsed)
    {
        var expected = expectedCollapsed ? "true" : "false";
        await page.WaitForFunctionAsync(
            $"expected => document.querySelector('#plan-rail')?.classList.contains('collapsed') === (expected === 'true')",
            expected,
            new PageWaitForFunctionOptions { Timeout = 10_000 });
    }

    private static async Task AssertRailCollapsedNowAsync(IPage page, bool expectedCollapsed)
    {
        if (expectedCollapsed)
        {
            await Assertions.Expect(page.Locator("#plan-rail")).ToHaveClassAsync(
                new System.Text.RegularExpressions.Regex("\\bcollapsed\\b"));
        }
        else
        {
            await Assertions.Expect(page.Locator("#plan-rail")).Not.ToHaveClassAsync(
                new System.Text.RegularExpressions.Regex("\\bcollapsed\\b"));
        }
    }
}
