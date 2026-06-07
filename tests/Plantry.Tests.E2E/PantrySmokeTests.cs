using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;
using Aspire.Hosting.Testing;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E smoke tests (Playwright).
///
/// PHASE-1-PLAN.md done-when for Slice 0:
///   "E2E smoke (register → login → see empty Pantry) passes"
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

    [Fact(DisplayName = "Register → Login → See empty Pantry")]
    public async Task RegisterLoginSeePantry()
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

            // ── Step 2: Should be redirected to Pantry ────────────────────────────
            await page.WaitForURLAsync("**/Pantry**");

            // ── Step 3: Empty state is shown ──────────────────────────────────────
            var emptyTitle = await page.Locator(".empty-state__title").TextContentAsync();
            Assert.Contains("empty", emptyTitle, StringComparison.OrdinalIgnoreCase);

            // ── Step 4: Sign out, then sign back in ───────────────────────────────

            // 1. Click the button using a resilient locator
            await page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();

            // 2. Wait for the URL to contain Login AND allow for the ?ReturnUrl querystring
            await page.WaitForURLAsync("**/Account/Login**");

            // Proceed with signing back in
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");

            await page.WaitForURLAsync("**/Pantry**");

            var titleAfterLogin = await page.Locator(".topbar__title").TextContentAsync();
            Assert.Contains("Pantry", titleAfterLogin, StringComparison.OrdinalIgnoreCase);
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
