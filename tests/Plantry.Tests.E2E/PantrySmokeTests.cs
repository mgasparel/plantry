using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;
using Aspire.Hosting.Testing;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E smoke tests (Playwright).
///
/// PHASE-1-PLAN.md done-when for Slice 0 (updated post nav-IA rework):
///   "E2E smoke (register → login → land on Today home) passes"
///
/// Post nav-IA rework (plantry-8r6 + plantry-pqu), /Today is the app home.
/// Register and login both redirect there instead of /Pantry.
///
/// Boots the whole service graph from the Aspire AppHost via AppHostFixture —
/// no manually started app instance required.
///
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class PantrySmokeTests(AppHostFixture appHost) : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    private string BaseUrl => appHost.BaseUrl;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }

    [Fact(DisplayName = "Register → Login → See Today home")]
    public async Task RegisterLoginSeeTodayHome()
    {
        var uniqueEmail = $"smoke-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true
        });

        await context.Tracing.StartAsync(new()
        {
            Screenshots = true,
            Snapshots = true,
            Sources = true
        });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── Step 1: Register ──────────────────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");

            await page.FillAsync("[name='Input.HouseholdName']", "Smoke Test Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "Smoke User");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");

            // ── Step 2: Should be redirected to Today home ────────────────────────
            await page.WaitForURLAsync("**/Today**");

            // ── Step 3: Today home is shown ───────────────────────────────────────
            // The new Today page renders a greeting header (.today-head__greeting) rather than
            // a catalog-section__heading stub. Verify the page loaded by checking the topbar
            // crumb title and the today-wrap container.
            await page.Locator(".today-wrap").WaitForAsync();
            var crumbTitle = await page.Locator(".crumb b").TextContentAsync();
            Assert.Contains("Today", crumbTitle, StringComparison.OrdinalIgnoreCase);

            // ── Step 4: Sign out, then sign back in ───────────────────────────────

            // 1. Click the button using a resilient locator
            await page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();

            // 2. Wait for the URL to contain Login AND allow for the ?ReturnUrl querystring
            await page.WaitForURLAsync("**/Account/Login**");

            // Proceed with signing back in
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");

            await page.WaitForURLAsync("**/Today**");

            await page.Locator(".today-wrap").WaitForAsync();
            var crumbTitleAfterLogin = await page.Locator(".crumb b").TextContentAsync();
            Assert.Contains("Today", crumbTitleAfterLogin, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await context.Tracing.StopAsync(new()
            {
                Path = "trace.zip"
            });
        }
    }
}
