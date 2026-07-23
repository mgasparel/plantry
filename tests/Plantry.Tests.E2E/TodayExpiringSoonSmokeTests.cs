using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Plantry.Web.Dev;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E smoke test for the expiring-soon widget on the Today (Home) page (SPEC Page 0 §0d,
/// plantry-81x acceptance criteria):
///
///   "A household with an expiring lot sees it on Home."
///
/// Converted (plantry-abq5) from UI-authored setup to the seed-then-navigate path: rather than
/// registering a fresh household and authoring a product + stock entry through the Pantry editor,
/// seed the demo household (<see cref="DevSeedHelper.SeedDemoDataAsync"/> — additive, idempotent) and
/// sign in AS the demo user to reach its pre-seeded expiring stock ("Chicken breast", 2 days out —
/// see <see cref="FakeDataSeeder"/>'s inventory stock plan). Demo LOGIN replaces registration here
/// because the seed endpoint targets a fixed demo household, not the calling session's — see
/// <see cref="DevSeedHelper"/>'s doc comment for the verified mechanic.
///
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class TodayExpiringSoonSmokeTests(AppHostFixture appHost) : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    private string BaseUrl => appHost.BaseUrl;

    /// <summary>Seeded (FakeDataSeeder.SeedInventoryAsync) with an expiry 2 days out — within the
    /// default expiring-soon horizon.</summary>
    private const string ProductName = "Chicken breast";

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

    [Fact(DisplayName = "Today: household with expiring stock sees it in the expiring-soon widget (plantry-81x/AC1)")]
    public async Task Today_ExpiringSoon_ShowsProductWithExpiryInWidget()
    {
        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── 1. Seed the demo household (additive, idempotent), then sign in as the demo user ──
            await DevSeedHelper.SeedDemoDataAsync(page, BaseUrl);

            await page.GotoAsync($"{BaseUrl}/Account/Login");
            await page.WaitForURLAsync("**/Account/Login**");
            await page.FillAsync("[name='Input.Email']", FakeDataSeeder.DemoEmail);
            await page.FillAsync("[name='Input.Password']", FakeDataSeeder.DemoPassword);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── 2. Navigate to Today and verify expiring-soon widget ───────────────
            await page.GotoAsync($"{BaseUrl}/Today");
            await page.WaitForURLAsync("**/Today**");
            await page.Locator(".today-wrap").WaitForAsync();

            // The expiring-soon widget must be visible (not cold-start, not all-clear)
            var widget = page.Locator(".today-exp-widget");
            await Assertions.Expect(widget).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

            // The seeded product must appear in the expiring list
            var expRow = page.Locator(".today-exp-row", new() { HasText = ProductName });
            await Assertions.Expect(expRow).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });

            // A day-pill badge is present — the unified expiry pill (plantry-fdoq); its relative
            // wording could be "Today", "Tomorrow", "in Nd", or "Expired Nd ago".
            var dayPill = expRow.Locator(".badge-expiry");
            await Assertions.Expect(dayPill).ToBeVisibleAsync();

            // The widget's count badge is > 0 (not calm/greyed-out for zero items)
            var countBadge = widget.Locator(".today-count-badge");
            await Assertions.Expect(countBadge).ToBeVisibleAsync();
            var badgeText = await countBadge.TextContentAsync();
            Assert.NotEqual("0", badgeText?.Trim());

            // The populated foot offers both actions: the secondary "Review all in pantry"
            // link and the primary "Use these up" deep-link into the Recipes browse.
            var pantryLink = widget.Locator(".today-widget__foot a", new() { HasText = "pantry" });
            await Assertions.Expect(pantryLink).ToBeVisibleAsync();

            // "Use these up" deep-links to the Recipes browse pre-filtered to "use soon"
            // (plantry-w1e). Confirm the link target carries the soon filter.
            var useUpLink = widget.Locator(".today-widget__foot a.today-widget__foot-cta");
            await Assertions.Expect(useUpLink).ToBeVisibleAsync();
            await Assertions.Expect(useUpLink).ToContainTextAsync("Use these up");
            await Assertions.Expect(useUpLink).ToHaveAttributeAsync("href", "/Recipes?soon=true");

            // Following it lands on the Recipes browse with the "Use soon" filter active —
            // the deep-link landing on the correctly-filtered browse (plantry-w1e AC).
            await useUpLink.ClickAsync();
            await page.WaitForURLAsync("**/Recipes**");
            await page.Locator(".recipes-browse-wrap").WaitForAsync();

            // The "Use soon" toggle renders in its active (warning) state, proving the
            // browse arrived with UseSoon bound on from the deep link.
            var activeSoonChip = page.Locator(".filter-chip.filter-chip--warning.is-active", new() { HasText = "Use soon" });
            await Assertions.Expect(activeSoonChip).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-today-expiring-soon.zip" });
        }
    }
}
