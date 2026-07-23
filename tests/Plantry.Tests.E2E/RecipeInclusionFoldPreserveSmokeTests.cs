using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Plantry.Web.Dev;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// E2E smoke for the inclusion-fold-preserve script (plantry-4037, plantry-fd0x): proves the plain-DOM
/// <c>htmx:oobBeforeSwap</c> / <c>htmx:oobAfterSwap</c> capture-and-restore glue in
/// <c>Details.cshtml</c> @section Scripts actually survives a real browser round-trip of the servings
/// stepper's <c>#rd-ing-rows</c> OOB swap — coverage <c>RecipeInclusionRollupRowTests</c>
/// (Plantry.Tests.Web) cannot give, since it only proves the fold is PRESENT in the OOB fragment, not
/// that browser-side JS re-opens it.
///
/// Setup uses the plantry-abq5 pre-seed path (<see cref="DevSeedHelper"/>): POST /Dev/Seed, then sign
/// in AS the demo user (the endpoint always seeds its own fixed demo household — see
/// <see cref="DevSeedHelper"/>'s doc comment — so this is demo LOGIN, not a fresh registration) and
/// drive the seeded parent-with-inclusion recipe
/// (<see cref="FakeDataSeeder.InclusionParentRecipeName"/>, "Beef Meatballs with Tomato Sauce",
/// including <see cref="FakeDataSeeder.InclusionSubRecipeName"/>, "Basic Tomato Sauce"). No UI recipe
/// authoring.
///
/// Journey:
///   Seed + sign in as demo → open the seeded parent recipe → expand its one inclusion fold → click the
///   servings stepper (triggers the #rd-ing-rows OOB swap) → assert the SAME #incl-fold-{id} is still
///   open after the swap → collapse the fold → step servings again → assert the fold stays closed after
///   this second swap (pins capture-and-restore working from both sides, per the ticket's TEST SHAPE
///   step 6 — re-running with the fold collapsed rather than authoring a second inclusion).
///
/// Swap-completion signal: a <c>htmx:oobAfterSwap</c> listener (mirroring the app's own fold-preserve
/// script detection) increments a page-global counter; each stepper click is waited out via
/// <c>WaitForFunctionAsync</c> on that counter rather than on any Alpine-scaled amount text, since the
/// ingredient amount's <c>x-text</c> re-renders immediately from the click (client-side servings scale)
/// independent of when the OOB swap actually lands.
///
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class RecipeInclusionFoldPreserveSmokeTests(AppHostFixture appHost) : IAsyncLifetime
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

    // Global JS listener installed on the page mirrors the app's own fold-preserve script's target
    // detection (Details.cshtml @section Scripts, rdIngRowsEl helper) so the test waits on the exact
    // same event the shipped feature relies on, not a proxy signal.
    private const string InstallOobSwapCounterScript = """
        () => {
            window.__oobSwapCount = 0;
            function rdIngRowsEl(el) {
                if (!el) return null;
                if (el.id === 'rd-ing-rows') return el;
                return el.closest ? el.closest('#rd-ing-rows') : null;
            }
            document.body.addEventListener('htmx:oobAfterSwap', function (evt) {
                if (rdIngRowsEl(evt.target)) window.__oobSwapCount++;
            });
        }
        """;

    [Fact(DisplayName = "Recipe Details: inclusion fold open/closed state survives the servings-stepper OOB swap (plantry-fd0x)")]
    public async Task InclusionFold_SurvivesServingsStepperOobSwap()
    {
        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── 1. Seed the demo household (additive, idempotent), then sign in as the demo user ──
            // /Dev/Seed always targets its own fixed demo household (DevSeedHelper's verified mechanic),
            // so this is demo LOGIN — not a fresh registration — per the plantry-abq5 pre-seed path.
            await DevSeedHelper.SeedDemoDataAsync(page, BaseUrl);

            await page.GotoAsync($"{BaseUrl}/Account/Login");
            await page.WaitForURLAsync("**/Account/Login**");
            await page.FillAsync("[name='Input.Email']", FakeDataSeeder.DemoEmail);
            await page.FillAsync("[name='Input.Password']", FakeDataSeeder.DemoPassword);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── 2. Navigate straight to the seeded parent-with-inclusion recipe ───────────────────
            await page.GotoAsync($"{BaseUrl}/Recipes?q={Uri.EscapeDataString(FakeDataSeeder.InclusionParentRecipeName)}");
            await page.WaitForURLAsync("**/Recipes**");

            var recipeCard = page.Locator("a.recipe-card", new() { HasText = FakeDataSeeder.InclusionParentRecipeName });
            await Assertions.Expect(recipeCard).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            await recipeCard.ClickAsync();
            await page.WaitForURLAsync(new Regex(@"/Recipes/[0-9a-fA-F-]+$"));
            await page.Locator("#rd-ing-rows").WaitForAsync();

            // Install the OOB-swap counter before any interaction that could trigger a swap.
            await page.EvaluateAsync(InstallOobSwapCounterScript);

            // ── 3. Locate the seeded recipe's one inclusion fold ──────────────────────────────────
            var fold = page.Locator("#rd-ing-rows details.rd-sub-fold");
            await Assertions.Expect(fold).ToHaveCountAsync(1);
            await Assertions.Expect(fold).ToBeVisibleAsync();

            var foldId = await fold.GetAttributeAsync("id");
            Assert.False(string.IsNullOrWhiteSpace(foldId));
            Assert.StartsWith("incl-fold-", foldId);

            // Starts collapsed (default fold state).
            Assert.False(await fold.EvaluateAsync<bool>("el => el.open"));

            // ── 4. Expand the fold — click the chevron (inside <summary>, but not the sub-recipe name
            //    link, whose own click handler stops propagation and would otherwise navigate away) ──
            await fold.Locator(".rd-sub-fold__chev").ClickAsync();
            Assert.True(await fold.EvaluateAsync<bool>("el => el.open"));

            // ── 5. Step servings up — triggers the debounced refreshFulfilment() → #rd-ing-rows OOB
            //    swap. Wait for the swap itself (not any Alpine-scaled text) to complete. ──
            await page.Locator(".stepper__btn[aria-label='More servings']").ClickAsync();
            await page.WaitForFunctionAsync("window.__oobSwapCount === 1",
                null, new PageWaitForFunctionOptions { Timeout = 30000 });

            // The fold-preserve script must have re-applied `open` to the SAME id in the fresh content.
            var foldAfterFirstSwap = page.Locator($"#{foldId}");
            await Assertions.Expect(foldAfterFirstSwap).ToHaveCountAsync(1);
            Assert.True(await foldAfterFirstSwap.EvaluateAsync<bool>("el => el.open"));

            // ── 6. Collapse the fold again, step servings a second time, and assert the fold-preserve
            //    script does NOT force it back open — pins capture-and-restore working from both sides
            //    (TEST SHAPE step 6) using the seeded recipe's single inclusion, re-run collapsed. ──
            await foldAfterFirstSwap.Locator(".rd-sub-fold__chev").ClickAsync();
            Assert.False(await foldAfterFirstSwap.EvaluateAsync<bool>("el => el.open"));

            await page.Locator(".stepper__btn[aria-label='More servings']").ClickAsync();
            await page.WaitForFunctionAsync("window.__oobSwapCount === 2",
                null, new PageWaitForFunctionOptions { Timeout = 30000 });

            var foldAfterSecondSwap = page.Locator($"#{foldId}");
            await Assertions.Expect(foldAfterSecondSwap).ToHaveCountAsync(1);
            Assert.False(await foldAfterSecondSwap.EvaluateAsync<bool>("el => el.open"));
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-recipe-inclusion-fold-preserve.zip" });
        }
    }
}
