using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E Playwright journey tests for the P3-2 "Set dietary preferences" feature.
/// Acceptance criterion L5: login → navigate to /Settings/Preferences → set a stance → change
/// it → clear it (set to Neutral); verify Neutral means no row is stored (DB check via
/// /Settings/Preferences re-render showing no stance selected).
///
/// The test sequence mirrors the J7 journey described in the issue:
///   1. Register &amp; login.
///   2. Navigate to /Settings → Dietary preferences link.
///   3. Set a stance on the first visible tag (click a non-Neutral segment).
///   4. Change that stance (click a different non-Neutral segment).
///   5. Clear it (click the Neutral segment / re-click same to trigger Neutral POST).
///   6. Verify the tag row shows Neutral (no segment selected / none highlighted).
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class DietaryPreferencesJourneyTests(AppHostFixture appHost) : IAsyncLifetime
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

    [Fact(DisplayName = "Login → Settings → Preferences page renders with grouped tag rows")]
    public async Task LoginNavigateSeePreferencesPage()
    {
        var uniqueEmail = $"prefs-smoke-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── Register ──────────────────────────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", "Prefs Smoke Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "Prefs Smoke");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── Navigate via Settings hub link ────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Settings");
            await page.WaitForURLAsync("**/Settings**");

            // The hub should contain the "Dietary preferences" link in the Meal Planning section.
            var prefLink = page.GetByRole(AriaRole.Link, new() { Name = "Dietary preferences" });
            await Assertions.Expect(prefLink).ToBeVisibleAsync();
            await prefLink.ClickAsync();
            await page.WaitForURLAsync("**/Settings/Preferences**");

            // ── Preferences page structure ────────────────────────────────────────
            // The cfg header must render.
            await Assertions.Expect(page.Locator("h1")).ToContainTextAsync("Dietary preferences");

            // At least one tag category card must be rendered (seeded tags by DM-9).
            await Assertions.Expect(page.Locator(".tag-cat").First).ToBeVisibleAsync();

            // At least one tag row must be rendered.
            await Assertions.Expect(page.Locator(".tag-row").First).ToBeVisibleAsync();

            // The stance scale (5 segments) must exist in the first tag row.
            var firstRow = page.Locator(".tag-row").First;
            await Assertions.Expect(firstRow.Locator(".stance-seg")).ToHaveCountAsync(5);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-prefs-smoke.zip" });
        }
    }

    [Fact(DisplayName = "Stance click live-updates the prefs-meta counter without a full reload (OOB swap)")]
    public async Task StanceClick_LiveUpdatesPrefsMetaCounter_WithoutFullReload()
    {
        var uniqueEmail = $"prefs-oob-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── Register ──────────────────────────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", "OOB Prefs Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "OOB User");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── Navigate to Preferences ───────────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Settings/Preferences");
            await page.WaitForURLAsync("**/Settings/Preferences**");

            // Wait for the prefs-meta block and at least one tag row to appear.
            await Assertions.Expect(page.Locator("#prefs-meta")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".tag-row").First).ToBeVisibleAsync();

            // Read the initial "N of M tags set" text from the prefs-meta block.
            // Fresh user → should be "0 of M tags set".
            var metaSpan = page.Locator("#prefs-meta span").Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex(@"of \d+ tags set") });
            var initialText = await metaSpan.InnerTextAsync();
            Assert.Contains("0 of", initialText);

            // ── Click the "Preferred" segment on the first tag row ─────────────────
            var firstRow = page.Locator(".tag-row").First;
            var preferredSeg = firstRow.Locator(".stance-seg").Nth(1); // Preferred
            await page.RunAndWaitForResponseAsync(
                async () => await preferredSeg.ClickAsync(),
                r => r.Url.Contains("Settings/Preferences") && r.Status == 200);

            // Wait for the htmx OOB swap to update the prefs-meta counter.
            // The counter should now read "1 of M tags set" — without a page reload.
            await Assertions.Expect(metaSpan).ToContainTextAsync("1 of");
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-prefs-oob.zip" });
        }
    }

    [Fact(DisplayName = "Set stance → change stance → clear stance (Neutral = no row stored)")]
    public async Task SetChangeAndClearStance_Journey()
    {
        var uniqueEmail = $"prefs-journey-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── Register ──────────────────────────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", "Prefs Journey Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "Journey User");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── Navigate to Preferences ───────────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Settings/Preferences");
            await page.WaitForURLAsync("**/Settings/Preferences**");

            // Wait for at least one tag row to appear.
            await Assertions.Expect(page.Locator(".tag-row").First).ToBeVisibleAsync();

            // ── Step 1: Set stance to "Preferred" on first tag row ────────────────
            var firstRow = page.Locator(".tag-row").First;
            // Segments: Required(0) Preferred(1) Neutral(2) Disliked(3) Restricted(4)
            var preferredSeg = firstRow.Locator(".stance-seg").Nth(1); // Preferred
            await page.RunAndWaitForResponseAsync(
                async () => await preferredSeg.ClickAsync(),
                r => r.Url.Contains("Settings/Preferences") && r.Status == 200);

            // The row should now exist and show a non-neutral state (.tag-row.touched)
            // after the htmx swap replaces the row content.
            await Assertions.Expect(page.Locator(".tag-row.touched").First).ToBeVisibleAsync();

            // ── Step 2: Change stance to "Required" on the same row ───────────────
            // After the htmx swap the row element was replaced; re-locate it.
            firstRow = page.Locator(".tag-row").First;
            var requiredSeg = firstRow.Locator(".stance-seg").Nth(0); // Required
            await page.RunAndWaitForResponseAsync(
                async () => await requiredSeg.ClickAsync(),
                r => r.Url.Contains("Settings/Preferences") && r.Status == 200);

            await Assertions.Expect(page.Locator(".tag-row.touched").First).ToBeVisibleAsync();

            // ── Step 3: Clear stance → Neutral ────────────────────────────────────
            firstRow = page.Locator(".tag-row").First;
            var neutralSeg = firstRow.Locator(".stance-seg").Nth(2); // Neutral
            await page.RunAndWaitForResponseAsync(
                async () => await neutralSeg.ClickAsync(),
                r => r.Url.Contains("Settings/Preferences") && r.Status == 200);

            // After clearing to Neutral the row must NOT have the touched modifier.
            // Wait for the swap to complete first using retrying assertion.
            await Assertions.Expect(page.Locator(".tag-row.touched")).ToHaveCountAsync(0);

            // ── Step 4: Verify DB side-effect via Settings/Preferences re-render ──
            // Navigate away and back to force a fresh GET which reads from the DB.
            await page.GotoAsync($"{BaseUrl}/Settings");
            await page.GotoAsync($"{BaseUrl}/Settings/Preferences");
            await page.WaitForURLAsync("**/Settings/Preferences**");
            await Assertions.Expect(page.Locator(".tag-row").First).ToBeVisibleAsync();

            // No tag-row should carry the touched class because all stances are Neutral
            // (the DB row was deleted by the SetStance("Neutral") call).
            var touchedCount = await page.Locator(".tag-row.touched").CountAsync();
            Assert.Equal(0, touchedCount);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-prefs-journey.zip" });
        }
    }
}
