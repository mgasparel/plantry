using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E coverage for &lt;searchable-select&gt;'s Enter-key select path (plantry-p5ww).
///
/// The component's keyboard-select flow (wwwroot/js/searchable-select.js) has two branches that
/// no unit test exercises directly, because it is classic (non-island) Alpine JS with no jsdom
/// harness (ADR-020 sanctioned Node's built-in runner for islands only, and named Playwright E2E
/// as the story for non-island JS):
///   • chooseHighlighted() delegates to the highlighted (or first) option's real DOM click rather
///     than calling select() directly, so Enter-to-choose stays correct for every host.
///   • select() guards $refs.hidden so it is safe to call in the unbound (no asp-for) mode added by
///     plantry-gzro.1, where there is no hidden input to write.
///
/// Bound mode is driven against a real production host (Pantry's Add-stock sheet). Unbound mode has
/// no production host that reaches select() by keyboard (the only unbound host,
/// _ProductSearchCreateSheet, overrides Enter with its own handler), so it is driven against the
/// unbound demo added to the Dev component gallery (/Dev), whose options use the component's default
/// click/keyboard handling.
///
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class SearchableSelectEnterKeyE2ETests(AppHostFixture appHost) : IAsyncLifetime
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

    [Fact(DisplayName = "searchable-select bound: type → Enter selects first option → hidden input gets the ProductId + label updates")]
    public async Task BoundMode_EnterKey_SelectsViaRealClick_WritesHiddenAndLabel()
    {
        var uniqueEmail = $"ss-bound-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";
        var productName = $"SS Enter Flour {Guid.NewGuid():N}".Substring(0, 22);

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            var jsErrors = new List<string>();
            page.PageError += (_, e) => jsErrors.Add(e);

            // ── Register a household (lands on Today, logged in) ──────────────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", "SS Enter Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "SS User");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── Create a stock-holding product so it is searchable in Add stock ──
            await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
            await page.WaitForURLAsync("**/Catalog/Products/Create");
            await page.FillAsync("[name='Input.Name']", productName);
            await page.SelectOptionAsync("[name='Input.DefaultUnitId']", new SelectOptionValue { Index = 1 });
            await page.ClickAsync("button[type=submit]:has-text('Add product')");
            await page.WaitForURLAsync("**/Catalog/**");

            // ── Open the Pantry Add-stock sheet (bound searchable-select host) ───
            await page.GotoAsync($"{BaseUrl}/Pantry");
            await page.WaitForURLAsync("**/Pantry**");
            await page.ClickAsync("button:has-text('Add stock')");
            await Assertions.Expect(page.Locator("#sheet-host .sheet__panel")).ToBeVisibleAsync();

            var combobox = page.Locator("#sheet-host .sheet__panel input[role='combobox']");
            var hidden = page.Locator("#sheet-host .sheet__panel input[name='Input.ProductId']");

            // Hidden input starts empty — nothing selected yet.
            await Assertions.Expect(hidden).ToHaveValueAsync("");

            // Type the full (unique) product name so the matching option is the first/only one, then
            // wait for the htmx-swapped option to render.
            await combobox.FillAsync(productName);
            var option = page.Locator("#sheet-host .sheet__panel .searchable-select__listbox li[role='option']",
                new() { HasText = productName });
            await Assertions.Expect(option).ToBeVisibleAsync();

            // Press Enter (NOT a click) — exercises chooseHighlighted() → first option's real click →
            // select(). highlighted is -1 (nothing arrowed to), so chooseHighlighted falls back to opts[0].
            await combobox.PressAsync("Enter");

            // select() wrote the option's data-value (the ProductId) into the bound hidden input …
            await Assertions.Expect(hidden).ToHaveValueAsync(new Regex(".+"));
            // … and updated the visible label to the option's text.
            await Assertions.Expect(combobox).ToHaveValueAsync(productName);
            // The popover closed after selection.
            await Assertions.Expect(page.Locator("#sheet-host .sheet__panel .searchable-select__listbox"))
                .Not.ToBeVisibleAsync();

            // The whole keyboard-select path ran with no uncaught JS error.
            Assert.Empty(jsErrors);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-ss-enter-bound.zip" });
        }
    }

    [Fact(DisplayName = "searchable-select unbound: type → Enter → select() no-ops the hidden write safely (no exception, no hidden input) + label updates")]
    public async Task UnboundMode_EnterKey_SelectNoOpsHiddenGuard_Safely()
    {
        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            var jsErrors = new List<string>();
            page.PageError += (_, e) => jsErrors.Add(e);

            // The unbound demo lives on the Dev component gallery (development-only, no auth required).
            await page.GotoAsync($"{BaseUrl}/Dev");
            await page.WaitForURLAsync("**/Dev**");

            // Scope everything to the unbound component (id="unbound-ss-demo" → listbox
            // id="unbound-ss-demo-listbox"), so the bound demo elsewhere on the page can't be matched.
            var combobox = page.Locator("[aria-controls='unbound-ss-demo-listbox']");
            await Assertions.Expect(combobox).ToBeVisibleAsync();

            // The unbound component renders NO hidden input (that is the whole point of the $refs.hidden
            // guard) — assert that before and after selection.
            var component = page.Locator("div.searchable-select:has([aria-controls='unbound-ss-demo-listbox'])");
            await Assertions.Expect(component.Locator("input[type='hidden']")).ToHaveCountAsync(0);

            // "Honey" is a single, unambiguous match in the demo grocery list → first/only option.
            // Type with real key events (PressSequentially, not Fill) so the component's htmx
            // `keyup changed` filter actually fires — Fill sets the value without a keyup, and the
            // focus-triggered empty-query swap only returns the first 10 groceries (Honey is 12th).
            await combobox.ClickAsync();
            await combobox.PressSequentiallyAsync("Honey");
            var option = page.Locator("#unbound-ss-demo-listbox li[role='option']", new() { HasText = "Honey" });
            await Assertions.Expect(option).ToBeVisibleAsync();

            // Baseline the JS-error count immediately before the Enter action. The /Dev gallery hosts
            // many unrelated demos, some of which log their own page errors on load; scoping to the
            // delta isolates "did the guarded select() path throw" from that pre-existing page noise.
            var errorsBeforeEnter = jsErrors.Count;

            // Press Enter — chooseHighlighted() → option.click() → select(). select() hits the
            // `if (this.$refs.hidden)` guard, finds no hidden ref, and skips the write instead of throwing.
            await combobox.PressAsync("Enter");

            // The visible label still updated (select() sets query after the guard) …
            await Assertions.Expect(combobox).ToHaveValueAsync("Honey");
            // … the popover closed (select() ran to completion past the guard) …
            await Assertions.Expect(page.Locator("#unbound-ss-demo-listbox")).Not.ToBeVisibleAsync();
            // … no hidden input was ever written into the component …
            await Assertions.Expect(component.Locator("input[type='hidden']")).ToHaveCountAsync(0);
            // … and the guarded select() path itself raised no new uncaught JS exception. (Label update
            // + popover close above already prove select() ran past the guard to completion; had the
            // guard been absent, `this.$refs.hidden.value = value` would have thrown before either.)
            Assert.Equal(errorsBeforeEnter, jsErrors.Count);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-ss-enter-unbound.zip" });
        }
    }
}
