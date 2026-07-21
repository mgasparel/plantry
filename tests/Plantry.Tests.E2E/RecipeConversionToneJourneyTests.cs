using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E — the landed-row conversion block's FILLED / MISSING tone toggle in the recipe editor
/// (plantry-wq9s, plantry-ac23). Snapshots prove BOTH tones' markup is emitted (the block is x-show,
/// so every branch is always in the DOM); only a real browser proves WHICH tone renders at runtime,
/// that the server-bounce hydration (<c>maybeHydrateRowConversion</c>) actually runs, and that the
/// MISSING → FILLED flip fires live when the author completes the equation inline. Those are the three
/// wirings this suite exercises:
///
///   <list type="number">
///     <item><description>
///       <b>Sheet-fill → neutral landing (AC1)</b>: the author states the cross-dimension fact in the
///       in-sheet prompt, commits, and the landed row renders the neutral confirmation (no warning
///       class, saved-note + derived echo visible, the MISSING ask hidden).
///     </description></item>
///     <item><description>
///       <b>Bounce → inline fix → save (AC2 + AC3)</b>: a save with an unfilled equation bounces; the
///       landed row shows the warning tone; hydration populates the axis selects and prefills the
///       recipe amount to 1 on the server-seeded row; filling the stock amount IN the landed row flips
///       the block live to neutral with no reload; re-saving lands on Details.
///     </description></item>
///   </list>
///
/// Each test registers a fresh household (unique email) and seeds a mass-default product, so runs are
/// independent and CI-safe. Picking a volume unit (cup) on the recipe line is the only scenario-shaping
/// requirement — it forces the cross-dimension gap that surfaces the ask.
///
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class RecipeConversionToneJourneyTests(AppHostFixture appHost) : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    private string BaseUrl => appHost.BaseUrl;

    /// <summary>The recipe Detail page URL (/Recipes/{guid}) — distinguishes Detail from the editor.</summary>
    private static readonly Regex DetailUrlPattern = new(@"/Recipes/[0-9a-fA-F-]{36}$");

    /// <summary>
    /// Selects a row currently in the warning (MISSING) tone. The block is x-show so BOTH tones' markup
    /// is always in the DOM; the row modifier class (added only while <c>needsConversion &amp;&amp; !filled</c>)
    /// is what actually distinguishes the rendered tone — a count of 1 means warning, 0 means neutral.
    /// </summary>
    private const string NeedsConversionRowSelector = ".ingredient-row.ingredient-row--needs-conversion";

    private const string SavedNoteText = "Saved to your catalog when you save the recipe.";

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        // The bounce path (POST → server re-render at the SAME url) gives no WaitForURL buffer, and the
        // hydration fetch runs async on x-init — so give web-first assertions room to poll across the
        // re-render and the async populate rather than tripping the 5s default on a cold Aspire stack.
        Assertions.SetDefaultExpectTimeout(30_000f);
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>Registers a fresh household and returns the page (landed on /Today).</summary>
    private async Task<IPage> RegisterHouseholdAsync(IBrowserContext context, string email, string householdName)
    {
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

        await page.GotoAsync($"{BaseUrl}/Account/Register");
        await page.WaitForURLAsync("**/Account/Register");
        await page.FillAsync("[name='Input.HouseholdName']", householdName);
        await page.FillAsync("[name='Input.Email']", email);
        await page.FillAsync("[name='Input.DisplayName']", "Test User");
        await page.FillAsync("[name='Input.Password']", "testpass1");
        await page.ClickAsync("button[type=submit]");
        await page.WaitForURLAsync("**/Today**");

        return page;
    }

    /// <summary>
    /// Seeds one catalog product whose DEFAULT unit is <paramref name="unitLabel"/> and returns the name.
    /// The product-create select renders options as "{code} — {name}" (e.g. "g — gram").
    /// </summary>
    private async Task SeedProductAsync(IPage page, string productName, string unitLabel)
    {
        await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
        await page.WaitForURLAsync("**/Catalog/Products/Create");
        await page.FillAsync("[name='Input.Name']", productName);
        await page.SelectOptionAsync("[name='Input.DefaultUnitId']", new SelectOptionValue { Label = unitLabel });
        await page.ClickAsync("button:has-text('Create Product')");
        await page.WaitForURLAsync("**/Catalog/Products/**");
    }

    /// <summary>
    /// Opens the add-ingredient sheet, picks the seeded product, sets the quantity, then selects a
    /// cross-dimension recipe-line unit (<paramref name="recipeUnitCode"/>) so the in-sheet conversion
    /// prompt fires. Leaves the sheet OPEN with the prompt shown; returns the sheet locator so the
    /// caller decides whether to fill the equation (AC1) or commit it blank to force a bounce (AC2).
    /// </summary>
    /// <remarks>
    /// Quantity is filled BEFORE the cross unit is chosen: at that point the prompt is absent, so
    /// <c>input[type='number']:visible</c> resolves to exactly the search-view Quantity (the two
    /// conv-equation amount inputs only appear once the prompt renders). Same for <c>select:visible</c>
    /// and the line-unit select. All conversion-prompt locators are scoped to the sheet — the landed
    /// row block (added on commit) carries identical aria-labels but lives outside <c>.sheet</c>.
    /// </remarks>
    private async Task<ILocator> StageCrossDimensionIngredientAsync(
        IPage page, string productName, string qty, string recipeUnitCode)
    {
        await page.ClickAsync("button:has-text('Add ingredient')");
        var sheet = page.Locator("#recipe-editor .sheet");
        await Assertions.Expect(sheet).ToBeVisibleAsync();

        // Type char-by-char so the htmx "keyup" search fires (FillAsync sets .value without keyup).
        await sheet.Locator("input[role='combobox']").PressSequentiallyAsync(productName.Substring(0, 8));
        var option = sheet.Locator(".searchable-select__listbox li[role='option']", new() { HasText = productName });
        await Assertions.Expect(option).ToBeVisibleAsync();
        await option.ClickAsync();

        // Product selected → unit defaults to the product's stock default (mass), so no prompt yet:
        // exactly one visible number input (Quantity) and one visible select (line unit).
        await sheet.Locator("input[type='number']:visible").FillAsync(qty);
        await sheet.Locator("select:visible").SelectOptionAsync(new SelectOptionValue { Label = recipeUnitCode });

        // Choosing a volume unit for a mass-default product is cross-dimension → the debounced
        // CheckConversion sets draft.needsConversion and the x-if prompt renders its amount inputs.
        await Assertions.Expect(sheet.Locator("input[aria-label='Stock amount']")).ToBeVisibleAsync();
        return sheet;
    }

    /// <summary>
    /// Turns the household's AI-assistance switch OFF via /Settings/Ai. With it off, a cross-dimension
    /// unit gap keeps today's manual C10 prompt (it does NOT defer to async AI seeding), so a save with
    /// an unfilled equation bounces with <c>NeedsConversion</c> — the exact path AC2 exercises. A fresh
    /// household defaults to ON, which would silently save-with-gap and never bounce.
    /// </summary>
    private async Task DisableAiAssistanceAsync(IPage page)
    {
        await page.GotoAsync($"{BaseUrl}/Settings/Ai");
        await page.WaitForURLAsync("**/Settings/Ai");
        // The radio is visually hidden (seg-ctrl); click the "Off" label, then persist.
        await page.Locator(".seg-ctrl__item", new() { HasText = "Off" }).ClickAsync();
        await page.ClickAsync("button[type=submit]:has-text('Save')");
        await Assertions.Expect(page.GetByText("Setting saved")).ToBeVisibleAsync();
    }

    /// <summary>Commits the staged sheet row via the search-view Add button and waits for the sheet to close.</summary>
    private static async Task CommitSheetAsync(ILocator sheet)
    {
        // Two .sheet__actions bars exist (search + create views); .First targets the search-view Add.
        await sheet.Locator(".sheet__actions button.btn--primary").First.ClickAsync();
        await Assertions.Expect(sheet).Not.ToBeVisibleAsync();
    }

    // ── Journey 1: sheet-fill lands neutral (AC1) ─────────────────────────────────

    [Fact(DisplayName = "AC1 Sheet-fill: cross-dimension equation stated in the sheet → landed row renders the neutral FILLED tone")]
    public async Task SheetFillCrossDimension_LandedRow_RendersNeutralFilledTone()
    {
        var email = $"recipe-conv-fill-{Guid.NewGuid():N}@test.local";

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await RegisterHouseholdAsync(context, email, "Recipe Conv Fill Household");

            // Product default unit = grams (mass) — picking "cup" (volume) on the line forces the ask.
            var productName = $"Cashews {Guid.NewGuid():N}".Substring(0, 20);
            await SeedProductAsync(page, productName, unitLabel: "g — gram");

            await page.GotoAsync($"{BaseUrl}/Recipes/New");
            await page.WaitForURLAsync("**/Recipes/New");
            await page.FillAsync("[name='Input.Name']", $"Nut Butter {Guid.NewGuid():N}".Substring(0, 20));
            await page.FillAsync("[name='Input.DefaultServings']", "2");

            // Stage the cross-dimension ingredient, then STATE the fact in the sheet: 120 g = 1 cup.
            var sheet = await StageCrossDimensionIngredientAsync(page, productName, qty: "2", recipeUnitCode: "cup");
            await sheet.Locator("input[aria-label='Stock amount']").FillAsync("120");
            // Recipe amount is prefilled to 1 by the sheet; the derived echo appears once both sides are set.
            await Assertions.Expect(sheet.GetByText(new Regex("Got it — so 1"))).ToBeVisibleAsync();
            await CommitSheetAsync(sheet);

            // ── Assert the landed row renders the neutral FILLED tone ──────────────
            var row = page.Locator(".ingredient-row").First;
            await Assertions.Expect(row).ToBeVisibleAsync();
            // Tone is keyed off the row class, NOT the inline :style background swap: neutral ⇒ no warning row.
            await Assertions.Expect(page.Locator(NeedsConversionRowSelector)).ToHaveCountAsync(0);

            var conv = row.Locator(".ingredient-row__conversion");
            await Assertions.Expect(conv).ToBeVisibleAsync();
            // Neutral confirmation: saved-on-save note + derived echo visible…
            await Assertions.Expect(conv.GetByText(SavedNoteText)).ToBeVisibleAsync();
            await Assertions.Expect(conv.GetByText(new Regex("Got it — so 1"))).ToBeVisibleAsync();
            // …and the MISSING warning ask hidden (x-show false → in DOM but not visible).
            await Assertions.Expect(conv.GetByText(new Regex("How does 1"))).ToBeHiddenAsync();
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-recipe-conv-fill.zip" });
        }
    }

    // ── Journey 2: bounce → inline fix → save (AC2 + AC3) ─────────────────────────

    [Fact(DisplayName = "AC2/AC3 Bounce: unfilled save bounces to warning tone (hydrated), inline fix flips live to neutral, re-save lands on Details")]
    public async Task UnfilledSaveBounces_InlineFixFlipsLive_ThenSavesToDetails()
    {
        var email = $"recipe-conv-bounce-{Guid.NewGuid():N}@test.local";

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await RegisterHouseholdAsync(context, email, "Recipe Conv Bounce Household");

            // Force the manual-prompt path: with AI assistance on, a cross-dimension gap defers to async
            // seeding and the save never bounces. Off keeps today's inline C10 prompt (AC4 of plantry-qll2.4).
            await DisableAiAssistanceAsync(page);

            var productName = $"Almonds {Guid.NewGuid():N}".Substring(0, 20);
            await SeedProductAsync(page, productName, unitLabel: "g — gram");

            await page.GotoAsync($"{BaseUrl}/Recipes/New");
            await page.WaitForURLAsync("**/Recipes/New");
            await page.FillAsync("[name='Input.Name']", $"Almond Milk {Guid.NewGuid():N}".Substring(0, 20));
            await page.FillAsync("[name='Input.DefaultServings']", "2");

            // Stage the cross-dimension ingredient but commit WITHOUT filling the equation — the row
            // lands carrying needsConversion, so the save will bounce.
            var sheet = await StageCrossDimensionIngredientAsync(page, productName, qty: "2", recipeUnitCode: "cup");
            await CommitSheetAsync(sheet);

            // ── First save bounces (AuthorRecipeResult.NeedsConversion re-renders the form) ──
            await page.ClickAsync("button[type=submit]:has-text('Create Recipe')");
            // The bounce sets the SaveError banner — wait for it so every assertion below runs against
            // the server-rendered DOM, not the pre-save client DOM (which also showed the MISSING tone).
            await Assertions.Expect(page.Locator(".save-error"))
                .ToContainTextAsync("need a conversion factor");

            var row = page.Locator(".ingredient-row").First;
            var conv = row.Locator(".ingredient-row__conversion");

            // ── AC2: warning tone on the bounced row ──────────────────────────────
            await Assertions.Expect(page.Locator(NeedsConversionRowSelector)).ToHaveCountAsync(1);
            await Assertions.Expect(conv.GetByText(new Regex("How does 1"))).ToBeVisibleAsync();
            await Assertions.Expect(conv.GetByText(SavedNoteText)).ToBeHiddenAsync();

            // ── AC3: hydration ran — server-seeded rows land with empty axis lists, so options can
            //         only be present if maybeHydrateRowConversion's CheckConversion fetch populated
            //         them; the recipe-side amount round-trips/prefills to 1. Auto-waiting assertions
            //         cover the async x-init fetch. ─────────────────────────────────
            await Assertions.Expect(row.Locator("select[aria-label='Stock unit'] option", new() { HasTextRegex = new Regex("^g$") }))
                .ToHaveCountAsync(1);
            await Assertions.Expect(row.Locator("select[aria-label='Recipe unit'] option", new() { HasText = "cup" }))
                .ToHaveCountAsync(1);
            await Assertions.Expect(row.Locator("input[aria-label='Recipe amount']")).ToHaveValueAsync("1");

            // ── AC2: fill the stock side IN the landed row → live flip to neutral, no reload ──
            await row.Locator("input[aria-label='Stock amount']").FillAsync("120");
            await Assertions.Expect(page.Locator(NeedsConversionRowSelector)).ToHaveCountAsync(0);
            await Assertions.Expect(conv.GetByText(SavedNoteText)).ToBeVisibleAsync();
            await Assertions.Expect(conv.GetByText(new Regex("How does 1"))).ToBeHiddenAsync();

            // ── Re-save now that the equation is complete → lands on Details ──────
            await page.ClickAsync("button[type=submit]:has-text('Create Recipe')");
            await page.WaitForURLAsync(DetailUrlPattern);
            Assert.DoesNotMatch(@"/(New|Edit)$", page.Url);
            await Assertions.Expect(page.Locator(".rd-ing-row", new() { HasText = productName })).ToBeVisibleAsync();
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-recipe-conv-bounce.zip" });
        }
    }
}
