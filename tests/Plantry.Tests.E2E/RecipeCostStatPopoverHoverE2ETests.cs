using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Plantry.Web.Dev;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// E2E render-level regression pin for plantry-fh2g: hovering the Recipe Details cost-stat's
/// missing-price flag must actually reveal the popover panel, not merely fire the CSS hover
/// reveal into a clipped ancestor.
///
/// ROOT CAUSE (see plantry-fh2g for the full writeup): <c>.rd-meta__val</c> set
/// <c>overflow: hidden; text-overflow: ellipsis</c>. The popover panel
/// (<c>.popover__content</c>) is absolutely positioned relative to the <c>.popover</c> wrapper
/// nested inside <c>.rd-meta__val</c>, so the panel's containing block sat inside the
/// overflow-hidden box — the hover reveal fired (opacity/visibility flipped) but the panel
/// dropped below the one-line-tall clip and was never visible. A prior HTML-snapshot test
/// (<see cref="RecipeDetailSnapshotTests"/> — Plantry.Tests.Web) already pinned the correct DOM
/// shape and still missed this, because DOM presence is not the same as being visibly rendered
/// after layout — only a real browser hover-and-measure proves that. The fix removed the
/// overflow clip from <c>.rd-meta__val</c> (kept <c>white-space: nowrap</c>).
///
/// Setup uses the plantry-abq5 pre-seed path (<see cref="DevSeedHelper"/>): POST /Dev/Seed, then
/// sign in AS the demo user, and open the seeded parent recipe
/// (<see cref="FakeDataSeeder.InclusionParentRecipeName"/>, "Beef Meatballs with Tomato Sauce").
/// That recipe naturally costs to <c>CostCompleteness.Partial</c> — its "Breadcrumbs" ingredient
/// is deliberately absent from <c>FakeDataSeeder.SeedPriceObservationsAsync</c>'s price table
/// while its other direct ingredients (Beef mince, Garlic, Olive oil) are priced — so no new
/// seed data or UI authoring is needed to exercise the Partial-state flag.
///
/// The None state (no costable ingredient priced) renders the identical flag/popover markup
/// inside the same <c>.rd-meta__val</c> element (Details.cshtml) and was governed by the exact
/// same CSS clip, so one render-level hover assertion here pins the regression for both states;
/// duplicating the journey for None would only re-prove the same CSS rule.
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class RecipeCostStatPopoverHoverE2ETests(AppHostFixture appHost) : IAsyncLifetime
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

    [Fact(DisplayName = "Recipe Details: hovering the Partial cost-stat flag reveals the popover panel (plantry-fh2g)")]
    public async Task PartialCostFlag_Hover_RevealsPopoverPanel()
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

            // ── 2. Navigate to the seeded parent recipe (Partial cost: Breadcrumbs un-priced) ─────
            await page.GotoAsync($"{BaseUrl}/Recipes?q={Uri.EscapeDataString(FakeDataSeeder.InclusionParentRecipeName)}");
            await page.WaitForURLAsync("**/Recipes**");

            var recipeCard = page.Locator("a.recipe-card", new() { HasText = FakeDataSeeder.InclusionParentRecipeName });
            await Assertions.Expect(recipeCard).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            await recipeCard.ClickAsync();
            await page.WaitForURLAsync(new Regex(@"/Recipes/[0-9a-fA-F-]+$"));

            // ── 3. Locate the cost-stat flag's popover ─────────────────────────────────────────────
            // .rd-meta__flag wraps the <popover> primitive on the Partial-cost cell.
            var flag = page.Locator(".rd-meta__flag .popover").First;
            await Assertions.Expect(flag).ToBeVisibleAsync();

            var trigger = flag.Locator("button.popover__trigger");
            var content = flag.Locator("span.popover__content");

            // Sanity: this is the Partial-estimate popover, not the None-state one.
            await Assertions.Expect(content).ToContainTextAsync("Partial estimate");
            await Assertions.Expect(content).ToContainTextAsync("Breadcrumbs");

            // ── 4. At rest the panel is present in the DOM but hidden (visibility/opacity) ─────────
            await Assertions.Expect(content).ToBeHiddenAsync();

            // ── 5. Hover the trigger — pins the render-level fix, not just DOM presence ────────────
            // Pre-fix, the hover reveal fired (opacity/visibility flipped) but the panel's
            // containing block sat inside .rd-meta__val's overflow:hidden clip, so it stayed
            // invisible; ToBeVisibleAsync checks an actual non-empty bounding box, which the old
            // HTML-snapshot coverage could never catch.
            await trigger.HoverAsync();
            await Assertions.Expect(content).ToBeVisibleAsync();

            var box = await content.BoundingBoxAsync();
            Assert.NotNull(box);
            Assert.True(box!.Width > 0 && box.Height > 0);

            // Render-level pin: ToBeVisibleAsync and a non-empty bounding box BOTH pass even when the
            // panel is clipped away by an ancestor overflow:hidden (getBoundingClientRect + Playwright
            // visibility ignore ancestor clipping). Hit-test the panel's own centre — elementFromPoint
            // respects overflow clipping, so a clipped panel resolves to a different element and fails
            // pre-fix (verified via CDP: clipped => elementFromPoint returns <html>, not the panel).
            var hitsPanel = await content.EvaluateAsync<bool>(@"el => {
                const r = el.getBoundingClientRect();
                const cx = r.left + r.width / 2, cy = r.top + r.height / 2;
                if (cx < 0 || cy < 0 || cx > innerWidth || cy > innerHeight) return false;
                const top = document.elementFromPoint(cx, cy);
                return top === el || (top !== null && el.contains(top));
            }");
            Assert.True(hitsPanel,
                "cost-stat popover panel is present but clipped by an ancestor overflow (plantry-fh2g regression) — its own centre does not hit-test to the panel");
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-recipe-cost-stat-popover-hover.zip" });
        }
    }
}
