using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E smoke test for the review-banner stack on the Today (Home) page
/// (SPEC Page 0 §0b, plantry-yb6 acceptance criteria):
///
///   "A Ready intake session renders a dismissible review banner linking to its review form."
///
/// Journey:
///   Register a fresh household → seed a catalog product → upload a receipt (fake parser
///   runs synchronously and marks the session Ready) → navigate away WITHOUT committing
///   → navigate to Today → verify the review banner is present and links to /Intake/Review/{id}.
///
/// The test-environment parser (<see cref="FakeReceiptParser"/>) stands in for the real AI:
/// the session transitions to Ready synchronously on upload, so the banner appears immediately
/// on the Today page.
///
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class TodayReviewBannerSmokeTests(AppHostFixture appHost) : IAsyncLifetime
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

    [Fact(DisplayName = "Today: Ready intake session shows a review banner linking to the review form (plantry-yb6/AC)")]
    public async Task Today_ReadyIntakeSession_ShowsReviewBannerLinkingToReviewForm()
    {
        var uniqueEmail = $"banner-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";
        var productName = $"BP {Guid.NewGuid():N}".Substring(0, 15);

        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── 1. Register a fresh household ─────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", "Banner Test Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "Banner Tester");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── 2. Seed a catalog product so the fake parser has a product to match ──
            await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
            await page.WaitForURLAsync("**/Catalog/Products/Create");
            await page.FillAsync("[name='Input.Name']", productName);
            await page.SelectOptionAsync("[name='Input.DefaultUnitId']", new SelectOptionValue { Label = "ea — each" });
            await page.ClickAsync("button:has-text('Add product')");
            await page.WaitForURLAsync("**/Catalog/Products/**");

            // ── 3. Upload a receipt — fake parser transitions session to Ready ──────
            // SetInputFilesAsync triggers the Alpine x-on:change handler which submits the form.
            // The fake parser runs synchronously and HX-Redirects to /Intake/Review/{id} (Ready).
            await page.GotoAsync($"{BaseUrl}/Intake/Upload");
            await page.WaitForURLAsync("**/Intake/Upload**");
            await page.SetInputFilesAsync("input[type=file][name='Receipt']", new FilePayload
            {
                Name = "receipt.png",
                MimeType = "image/png",
                Buffer = TinyPngBytes(),
            });

            // Wait for redirect to the review form — the session is now Ready.
            await page.WaitForURLAsync("**/Intake/Review/**");

            // ── 4. Navigate away WITHOUT committing → session stays Ready ─────────
            await page.GotoAsync($"{BaseUrl}/Today");
            await page.WaitForURLAsync("**/Today**");
            await page.Locator(".today-wrap").WaitForAsync();

            // ── 5. Verify the review banner stack appears ──────────────────────────
            // The banner stack renders when IsColdStart=false AND there is at least one Ready session.
            // At this point: we have a product (hasStock=false but hasPendingIntake=true so not cold-start)
            // and a Ready session, so the banner should be visible.
            var bannerStack = page.Locator(".today-banner-stack");
            await Assertions.Expect(bannerStack).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

            // The intake banner must carry the intake kind modifier
            var intakeBanner = bannerStack.Locator(".today-banner--intake").First;
            await Assertions.Expect(intakeBanner).ToBeVisibleAsync();

            // The banner title must mention item count
            var title = intakeBanner.Locator(".today-banner__title");
            await Assertions.Expect(title).ToBeVisibleAsync();
            var titleText = await title.TextContentAsync();
            Assert.NotNull(titleText);
            Assert.Contains("ready to review", titleText, StringComparison.OrdinalIgnoreCase);

            // The Review link must point to /Intake/Review/{id}
            var reviewLink = intakeBanner.Locator(".today-banner__review");
            await Assertions.Expect(reviewLink).ToBeVisibleAsync();
            var href = await reviewLink.GetAttributeAsync("href");
            Assert.NotNull(href);
            Assert.Matches(@"/Intake/Review/[0-9a-f\-]+", href);

            // A dismiss button must be present
            var dismissBtn = intakeBanner.Locator(".today-banner__dismiss");
            await Assertions.Expect(dismissBtn).ToBeVisibleAsync();

            // ── 6. Click Review — verify it navigates to the review form ───────────
            await reviewLink.ClickAsync();
            await page.WaitForURLAsync("**/Intake/Review/**");
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-today-review-banner.zip" });
        }
    }

    /// <summary>Smallest valid 1×1 PNG. The fake parser ignores the bytes, but the upload page
    /// enforces an image content type and a non-empty body, so a real (tiny) PNG keeps the
    /// upload path honest. Mirrors TinyPngBytes() in ReceiptIntakeJourneyTests.</summary>
    private static byte[] TinyPngBytes() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
}
