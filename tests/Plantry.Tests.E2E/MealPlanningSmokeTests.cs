using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E smoke tests (Playwright) — P3-0 done-when:
///   1. A logged-in user can navigate to /MealPlan and see the week-grid shell with
///      the seeded slot labels (Breakfast, Lunch, Dinner). This doubles as the
///      RlsMiddleware HTTP-level proof: if <c>RlsMiddleware</c> does NOT call
///      <c>mealPlanningDb.SetHouseholdId(id)</c>, the EF query filter returns nothing
///      and the grid renders empty (the slot labels would be absent), causing this
///      assertion to fail.
///   2. /Settings resolves (no longer a dead link from the footer and More page).
///
/// Boots the whole service graph via AppHostFixture.
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class MealPlanningSmokeTests(AppHostFixture appHost) : IAsyncLifetime
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

    [Fact(DisplayName = "Login → open Meal Plan page → seeded slot rows are visible (proves RlsMiddleware wiring)")]
    public async Task LoginNavigateSeeSlotRows()
    {
        var uniqueEmail = $"smoke-mp-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── Register a household (lands on Today home, logged in) ─────────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", "Smoke MealPlan Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "Smoke User");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── Navigate to Meal Plan via the sidebar nav entry ───────────────────
            await page.GetByRole(AriaRole.Link, new() { Name = "Meal Plan" }).First.ClickAsync();
            await page.WaitForURLAsync("**/MealPlan**");

            // ── Week grid renders with seeded slot labels ─────────────────────────
            // The plan-grid is present and contains the Breakfast/Lunch/Dinner slot
            // labels seeded by MealPlanningReferenceDataSeeder at registration.
            // If RlsMiddleware does NOT call mealPlanningDb.SetHouseholdId, the EF
            // query filter returns zero slots and the planner-empty state renders
            // instead — these assertions would fail, catching the gotcha.
            await Assertions.Expect(page.Locator(".plan-grid")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".plan-grid__slot-label")).ToHaveCountAsync(3);

            var slotLabels = await page.Locator(".plan-grid__slot-label").AllTextContentsAsync();
            Assert.Contains("Breakfast", slotLabels);
            Assert.Contains("Lunch", slotLabels);
            Assert.Contains("Dinner", slotLabels);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-mealplan.zip" });
        }
    }

    [Fact(DisplayName = "Login → /Settings resolves and renders the Settings hub (no longer a dead link)")]
    public async Task LoginNavigateSeeSettingsHub()
    {
        var uniqueEmail = $"smoke-settings-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── Register a household (lands on Today home, logged in) ─────────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", "Smoke Settings Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "Smoke User");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── Navigate to Settings (previously a dead link from the footer) ──────
            await page.GotoAsync($"{BaseUrl}/Settings");
            await page.WaitForURLAsync("**/Settings**");

            // ── Settings hub rendered successfully ────────────────────────────────
            var heading = await page.Locator("h2").First.TextContentAsync();
            Assert.Contains("Settings", heading, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-settings.zip" });
        }
    }
}
