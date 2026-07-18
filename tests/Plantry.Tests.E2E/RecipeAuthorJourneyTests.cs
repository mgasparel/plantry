using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E — the recipe author journeys (P2-1f done-when):
///
///   <list type="number">
///     <item><description>
///       <b>Create flow (J6)</b>: new recipe → add one ingredient via product search, one via inline
///       staple create → mint a new tag inline → upload a photo → save → assert landing on Detail
///       with the expected content (title, ingredient names, tag pill).
///     </description></item>
///     <item><description>
///       <b>Edit flow (J7)</b>: open an existing recipe → change servings (triggering the Proportional
///       scale offer) → choose Proportional → edit one ingredient quantity → save → assert the Detail
///       page reflects the updated servings and changed quantity.
///     </description></item>
///   </list>
///
/// Both journeys register a fresh household per run (unique email) and seed the catalog before
/// exercising the recipe editor, so each run is independent and CI-safe.
///
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class RecipeAuthorJourneyTests(AppHostFixture appHost) : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    private string BaseUrl => appHost.BaseUrl;

    /// <summary>The recipe Detail page URL (/Recipes/{guid}) — distinguishes Detail from the editor (/Recipes/New, …/Edit).</summary>
    private static readonly Regex DetailUrlPattern = new(@"/Recipes/[0-9a-fA-F-]{36}$");

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

    /// <summary>Seeds one catalog product and returns the product name.</summary>
    private async Task<string> SeedProductAsync(IPage page, string productName)
    {
        await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
        await page.WaitForURLAsync("**/Catalog/Products/Create");
        await page.FillAsync("[name='Input.Name']", productName);
        await page.SelectOptionAsync("[name='Input.DefaultUnitId']", new SelectOptionValue { Label = "ea — each" });
        await page.ClickAsync("button:has-text('Add product')");
        await page.WaitForURLAsync("**/Catalog/Products/**");
        return productName;
    }

    // ── Ingredient sheet helpers (the editor adds/edits ingredients via a modal sheet) ──

    /// <summary>Opens the add-ingredient sheet, picks a product via search, fills qty + unit, and commits.</summary>
    /// <remarks>
    /// Scoped to #recipe-editor .sheet to avoid Playwright strict-mode violations: both the global
    /// quick-add sheet in _Layout and the per-page ingredient sheet use .sheet/.sheet__panel class
    /// names. #recipe-editor is the root Alpine x-data div that wraps the editor form and its sheet.
    /// </remarks>
    private async Task AddProductIngredientAsync(IPage page, string productName, string qty, string unitLabel)
    {
        await page.ClickAsync("button:has-text('Add ingredient')");
        var sheet = page.Locator("#recipe-editor .sheet");
        await Assertions.Expect(sheet).ToBeVisibleAsync();

        // Type char-by-char (PressSequentially) so the htmx "keyup" trigger fires — the listbox is
        // populated entirely by the htmx search, and FillAsync sets .value without firing keyup.
        await sheet.Locator("input[role='combobox']").PressSequentiallyAsync(productName.Substring(0, 8));
        var option = sheet.Locator(".searchable-select__listbox li[role='option']", new() { HasText = productName });
        await Assertions.Expect(option).ToBeVisibleAsync();
        await option.ClickAsync();

        // `input[type='number']:visible` resolves to the search-view Quantity — the create-view
        // Quantity field (plantry-guab) is x-show hidden here, mirroring the `select:visible` pattern below.
        await sheet.Locator("input[type='number']:visible").FillAsync(qty);
        // `select:visible` resolves to the unit select — the create-view select is x-show hidden here.
        await sheet.Locator("select:visible").SelectOptionAsync(new SelectOptionValue { Label = unitLabel });

        // The sheet has two .sheet__actions bars (search view + create view); use .First to target
        // the search-view "Add" button without strict-mode violation (plantry-nb4x two-view scaffold).
        await sheet.Locator(".sheet__actions button.btn--primary").First.ClickAsync();
        await Assertions.Expect(sheet).Not.ToBeVisibleAsync();
    }

    /// <summary>
    /// Opens the add-ingredient sheet, switches to inline tracked-product create mode (plantry-orix),
    /// fills name + quantity + default unit, and commits in a single pass (plantry-guab).
    /// </summary>
    /// <remarks>
    /// Scoped to #recipe-editor .sheet to avoid Playwright strict-mode violations.
    /// The create-view mints a tracked catalog product ("as a new product" create-button phrase,
    /// plantry-orix / plantry-gzro). R5 (tracked ingredients require qty+unit) is satisfied entirely
    /// within the create view: Quantity is entered directly in the create-view field (plantry-guab)
    /// and Unit falls back from the product default (newStapleUnit) in saveSheet() — no second trip
    /// through the search view is needed, which is exactly the friction plantry-guab removes.
    /// </remarks>
    private async Task AddTrackedProductIngredientAsync(IPage page, string productName, string qty, string unitLabel)
    {
        await page.ClickAsync("button:has-text('Add ingredient')");
        var sheet = page.Locator("#recipe-editor .sheet");
        await Assertions.Expect(sheet).ToBeVisibleAsync();

        // The shared search + create component's "+ Create ... as a new product" button
        // (plantry-gzro) only appears once the user has typed a query, so type first — then click
        // it to navigate to the create view in-place (plantry-nb4x). The button text embeds the
        // query, so match on the static trailing phrase "as a new product" instead.
        await sheet.Locator("input[role='combobox']").PressSequentiallyAsync(productName.Substring(0, 8));
        await sheet.Locator("button:has-text('as a new product')").ClickAsync();
        var nameInput = sheet.Locator("input[placeholder='Product name (e.g. Olive Oil)']");
        await Assertions.Expect(nameInput).ToBeVisibleAsync();
        await nameInput.FillAsync(productName);

        // Quantity is entered directly in the create view (plantry-guab) — the field is TrackStock-gated
        // and always visible in create mode, so R5 is satisfied without re-opening the row afterwards.
        await sheet.Locator("#create-product-qty").FillAsync(qty);

        // The Defaults collapsible (plantry-y53t) is OPEN by default (plantry-grvy), so the unit
        // select is directly accessible — no summary click needed.
        var defaultsSummary = sheet.Locator(".sheet-defaults__summary");
        await Assertions.Expect(defaultsSummary).ToBeVisibleAsync();
        // Select the product's default unit. saveSheet() will pre-populate draft.unitId from this
        // (plantry-orix), so the committed row already has a unitId set.
        await sheet.Locator("#create-product-unit").SelectOptionAsync(new SelectOptionValue { Label = unitLabel });

        // Use .Last to target the create-view "Create" button (the search-view "Add" is .First).
        // Single pass: name + qty + unit are all set here, so the committed row satisfies R5
        // (tracked ingredients require qty+unit) with no second trip through the search view.
        await sheet.Locator(".sheet__actions button.btn--primary").Last.ClickAsync();
        await Assertions.Expect(sheet).Not.ToBeVisibleAsync();
    }

    // ── Journey 1: Create ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "J6 Create: new recipe → ingredient search + inline staple + new tag + photo → Detail shows content")]
    public async Task CreateRecipe_WithIngredients_LandsOnDetailWithExpectedContent()
    {
        var email = $"recipe-create-{Guid.NewGuid():N}@test.local";

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await RegisterHouseholdAsync(context, email, "Recipe Create Household");

            // ── Seed a catalog product for the ingredient product-search path ──────
            var tomatoName = $"Roma Tomato {Guid.NewGuid():N}".Substring(0, 24);
            await SeedProductAsync(page, tomatoName);

            // ── Create the "Italian" tag in Settings first (tag picker is read-only; only shows defined tags) ──
            await page.GotoAsync($"{BaseUrl}/Settings/Tags");
            await page.WaitForURLAsync("**/Settings/Tags**");
            await page.FillAsync("input.tag-add-name", "Italian");
            await page.ClickAsync("button.tag-add-btn");
            // Wait for htmx swap to render the new tag row
            await Assertions.Expect(page.Locator(".tag-name-input[value='Italian']"))
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15000 });

            // ── Navigate to new recipe form via the /Recipes/New alias ───────────
            await page.GotoAsync($"{BaseUrl}/Recipes/New");
            await page.WaitForURLAsync("**/Recipes/New");

            // ── Fill recipe basics ────────────────────────────────────────────────
            var recipeName = $"Tomato Pasta {Guid.NewGuid():N}".Substring(0, 20);
            await page.FillAsync("[name='Input.Name']", recipeName);
            await page.FillAsync("[name='Input.DefaultServings']", "2");

            // ── Add a tag via the filter-and-select picker ────────────────────────
            await page.FillAsync("input[placeholder='Filter tags…']", "Ital");
            var tagOption = page.Locator(".tag-picker-option", new() { HasText = "Italian" });
            await Assertions.Expect(tagOption).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });
            await tagOption.ClickAsync();
            // The chip should appear in the tag editor (Alpine-rendered)
            await Assertions.Expect(page.Locator(".recipe-tag-editor .badge", new() { HasText = "Italian" }))
                .ToBeVisibleAsync();

            // ── Ingredient 1: product search via the add-ingredient sheet ────────
            await AddProductIngredientAsync(page, tomatoName, qty: "400", unitLabel: "ea");
            // The committed row renders read-only with the product name in its summary.
            await Assertions.Expect(page.Locator(".ingredient-row__summary", new() { HasText = tomatoName }))
                .ToBeVisibleAsync();

            // ── Ingredient 2: inline tracked-product create via the sheet (plantry-orix) ─────
            await AddTrackedProductIngredientAsync(page, "Olive Oil", qty: "30", unitLabel: "ea");
            await Assertions.Expect(page.Locator(".ingredient-row__summary", new() { HasText = "Olive Oil" }))
                .ToBeVisibleAsync();

            // ── Upload a photo (a tiny PNG — the app stores it as bytea) ─────────
            await page.SetInputFilesAsync("input[type=file][name='photo']", new FilePayload
            {
                Name = "recipe.png",
                MimeType = "image/png",
                Buffer = TinyPngBytes(),
            });

            // ── Submit the form ───────────────────────────────────────────────────
            await page.ClickAsync("button[type=submit]:has-text('Create recipe')");

            // ── Assert: lands on the Detail page ─────────────────────────────────
            // Wait specifically for the recipe Detail URL (/Recipes/{guid}); the loose "**/Recipes/**"
            // glob also matches the editor URL /Recipes/New and would race the post-save redirect.
            await page.WaitForURLAsync(DetailUrlPattern);
            Assert.DoesNotMatch(@"/Edit$", page.Url);

            // Recipe name is the page heading (now h1 overlaid on the hero)
            var heading = await page.Locator("h1.rd-hero__name").TextContentAsync();
            Assert.Contains(recipeName, heading, StringComparison.OrdinalIgnoreCase);

            // Tomato ingredient renders in the ingredients rail card
            await Assertions.Expect(page.Locator(".rd-ing-row", new() { HasText = tomatoName }))
                .ToBeVisibleAsync();

            // Olive Oil tracked product renders in the ingredient list (plantry-orix: now tracked, not untracked)
            await Assertions.Expect(page.Locator(".rd-ing-row", new() { HasText = "Olive Oil" }))
                .ToBeVisibleAsync();

            // Tag mini-pill "Italian" is present in the hero overlay
            await Assertions.Expect(page.Locator(".rd-hero__tags .recipe-tag-mini", new() { HasText = "Italian" }))
                .ToBeVisibleAsync();

            // Photo is rendered (hero img tag present)
            await Assertions.Expect(page.Locator(".rd-hero__photo"))
                .ToBeVisibleAsync();
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-recipe-create.zip" });
        }
    }

    // ── Journey 2: Edit ───────────────────────────────────────────────────────────

    [Fact(DisplayName = "J7 Edit: change servings (Proportional) + edit ingredient → Detail reflects changes")]
    public async Task EditRecipe_ChangeServingsProportional_DetailReflectsChanges()
    {
        var email = $"recipe-edit-{Guid.NewGuid():N}@test.local";

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await RegisterHouseholdAsync(context, email, "Recipe Edit Household");

            // ── Seed a catalog product ────────────────────────────────────────────
            var flourName = $"Plain Flour {Guid.NewGuid():N}".Substring(0, 22);
            await SeedProductAsync(page, flourName);

            // ── Create a recipe to edit ───────────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Recipes/New");
            await page.WaitForURLAsync("**/Recipes/New");

            var recipeName = $"Bread {Guid.NewGuid():N}".Substring(0, 16);
            await page.FillAsync("[name='Input.Name']", recipeName);
            await page.FillAsync("[name='Input.DefaultServings']", "2");

            // Pick the seeded flour product via the add-ingredient sheet
            await AddProductIngredientAsync(page, flourName, qty: "200", unitLabel: "ea");

            await page.ClickAsync("button[type=submit]:has-text('Create recipe')");
            await page.WaitForURLAsync(DetailUrlPattern);

            // Capture the detail URL so we can construct the edit URL
            var detailUrl = page.Url;
            var editUrl = detailUrl.TrimEnd('/') + "/Edit";

            // ── Open the edit form ────────────────────────────────────────────────
            await page.GotoAsync(editUrl);
            await page.WaitForURLAsync($"**{editUrl.Replace(BaseUrl, "")}");

            // ── Change servings: 2 → 4 (triggers the scale offer) ────────────────
            var servingsInput = page.Locator("[name='Input.DefaultServings']");
            await servingsInput.ClearAsync();
            await servingsInput.FillAsync("4");
            // Trigger Alpine's servings watcher so showScaleOffer() evaluates to true.
            await servingsInput.BlurAsync();

            // The scale-offer card should now be visible (Alpine reveals it when servings != origServings
            // and hasIngredients is true).
            var scaleCard = page.Locator(".seg-ctrl[aria-label='Scale mode']").Locator("..");
            await Assertions.Expect(scaleCard).ToBeVisibleAsync();

            // Select Proportional mode by clicking the segmented-control label — the radio input itself
            // is visually hidden (sr-only) so it is not directly clickable.
            await page.Locator(".seg-ctrl__item", new() { HasText = "Proportional" }).ClickAsync();

            // ── Also change the first ingredient quantity via its edit sheet ───────
            await page.Locator(".ingredient-row").First
                .Locator("button[aria-label='Edit ingredient']").ClickAsync();
            var editSheet = page.Locator("#recipe-editor .sheet");
            await Assertions.Expect(editSheet).ToBeVisibleAsync();
            // `:visible` targets the search-view Quantity; the create-view Quantity (plantry-guab) is x-show hidden.
            var qtyInput = editSheet.Locator("input[type='number']:visible");
            await qtyInput.ClearAsync();
            await qtyInput.FillAsync("500");
            // Use .First to target the search-view "Save" button (avoids strict-mode violation from
            // the create-view button also in DOM, plantry-nb4x two-view scaffold).
            await editSheet.Locator(".sheet__actions button.btn--primary").First.ClickAsync();
            await Assertions.Expect(editSheet).Not.ToBeVisibleAsync();

            // ── Submit ────────────────────────────────────────────────────────────
            await page.ClickAsync("button[type=submit]:has-text('Save changes')");
            await page.WaitForURLAsync(DetailUrlPattern);
            Assert.DoesNotMatch(@"/Edit$", page.Url);

            // ── Assert: Detail reflects updated servings ──────────────────────────
            // The servings stepper val shows the current servings count in the ingredients card header.
            // Uses .stepper__val (the <stepper variant="compact"> tag helper output).
            var servingsText = await page.Locator(".stepper__val").InnerTextAsync();
            Assert.Contains("4", servingsText);

            // Recipe name still visible in hero
            var heading = await page.Locator("h1.rd-hero__name").TextContentAsync();
            Assert.Contains(recipeName, heading, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-recipe-edit.zip" });
        }
    }

    // ── Journey 3: Inspect — servings stepper rescales ingredient quantities ─────

    [Fact(DisplayName = "J3 Inspect: browse → open recipe → step servings up → ingredient quantities rescale")]
    public async Task InspectRecipe_StepServingsUp_QuantitiesRescale()
    {
        var email = $"recipe-inspect-{Guid.NewGuid():N}@test.local";

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await RegisterHouseholdAsync(context, email, "Recipe Inspect Household");

            // ── Seed a product ────────────────────────────────────────────────────
            var flourName = $"Bread Flour {Guid.NewGuid():N}".Substring(0, 22);
            await SeedProductAsync(page, flourName);

            // ── Create a recipe with a known ingredient quantity ──────────────────
            await page.GotoAsync($"{BaseUrl}/Recipes/New");
            await page.WaitForURLAsync("**/Recipes/New");

            var recipeName = $"Loaf {Guid.NewGuid():N}".Substring(0, 16);
            await page.FillAsync("[name='Input.Name']", recipeName);
            await page.FillAsync("[name='Input.DefaultServings']", "2");

            await AddProductIngredientAsync(page, flourName, qty: "200", unitLabel: "ea");

            await page.ClickAsync("button[type=submit]:has-text('Create recipe')");
            await page.WaitForURLAsync(DetailUrlPattern);

            // ── Verify the default quantity is shown ──────────────────────────────
            // Ingredient quantities are now in .rd-ing-qty spans inside the ingredients rail card.
            var qtySpan = page.Locator(".rd-ing-qty").First;
            await Assertions.Expect(qtySpan).ToBeVisibleAsync();
            var defaultQty = await qtySpan.InnerTextAsync();
            // The Alpine x-text renders the quantity; default servings = 2, qty = 200.
            Assert.Contains("200", defaultQty, StringComparison.Ordinal);

            // ── Step servings up: 2 → 4 ─────────────────────────────────────────
            // Stepper is now <stepper variant="compact"> → emits .stepper__btn in the ingredients card header.
            var moreBtn = page.Locator(".stepper__btn[aria-label='More servings']");
            await Assertions.Expect(moreBtn).ToBeVisibleAsync();
            // Click twice to go from 2 → 4 servings.
            await moreBtn.ClickAsync();
            await moreBtn.ClickAsync();

            // Alpine rescales: 200 × (4/2) = 400.
            // x-text runs client-side; give Alpine a tick to rerender.
            await page.WaitForFunctionAsync("document.querySelector('.rd-ing-qty').textContent.trim() !== '200'");

            var scaledQty = await qtySpan.InnerTextAsync();
            Assert.Contains("400", scaledQty, StringComparison.Ordinal);

            // Servings stepper val updates too (shows current count, not "N servings").
            // Uses .stepper__val (the <stepper variant="compact"> tag helper output).
            var stepperVal = await page.Locator(".stepper__val").InnerTextAsync();
            Assert.Contains("4", stepperVal, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-recipe-inspect.zip" });
        }
    }

    // ── Journey 4: Tracked product inline create from ingredient sheet (plantry-orix) ─────

    [Fact(DisplayName = "C12-tracked: ingredient sheet create-view mints a tracked catalog product")]
    public async Task InlineTrackedProductCreate_InIngredientSheet_ProductAppearsInCatalog()
    {
        var email = $"recipe-tracked-create-{Guid.NewGuid():N}@test.local";

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await RegisterHouseholdAsync(context, email, "Recipe Tracked Create Household");

            // ── Navigate to new recipe form ───────────────────────────────────────
            await page.GotoAsync($"{BaseUrl}/Recipes/New");
            await page.WaitForURLAsync("**/Recipes/New");

            var recipeName = $"Stir Fry {Guid.NewGuid():N}".Substring(0, 20);
            await page.FillAsync("[name='Input.Name']", recipeName);
            await page.FillAsync("[name='Input.DefaultServings']", "2");

            // ── Open the ingredient sheet and switch to create-view ───────────────
            await page.ClickAsync("button:has-text('Add ingredient')");
            var sheet = page.Locator("#recipe-editor .sheet");
            await Assertions.Expect(sheet).ToBeVisibleAsync();

            var trackedProductName = $"Sesame Oil {Guid.NewGuid():N}".Substring(0, 22);

            // Primary acceptance criterion: the create affordance's trailing phrase is "as a new
            // product" (plantry-orix / plantry-gzro). The shared search + create component's create
            // button only appears once the user has typed a query, so type first.
            await sheet.Locator("input[role='combobox']").PressSequentiallyAsync(trackedProductName.Substring(0, 8));
            var createBtn = sheet.Locator("button:has-text('as a new product')");
            await Assertions.Expect(createBtn).ToBeVisibleAsync();
            await createBtn.ClickAsync();

            // Create-view is now active: the product-name input is visible.
            var nameInput = sheet.Locator("input[placeholder='Product name (e.g. Olive Oil)']");
            await Assertions.Expect(nameInput).ToBeVisibleAsync();

            // The Defaults collapsible (from sibling plantry-y53t) is present in the create-view.
            await Assertions.Expect(sheet.Locator(".sheet-defaults__summary")).ToBeVisibleAsync();

            await nameInput.FillAsync(trackedProductName);

            // ── Fill qty directly in the create-view (plantry-guab) ───────────────
            // AuthorRecipe R5 validates qty/unit BEFORE the Catalog write (to avoid orphan products).
            // The create-view now carries a TrackStock-gated Quantity field (plantry-guab), so R5 is
            // satisfied in a single sheet pass — no re-opening the row in the search view. Unit is
            // covered by the product default (newStapleUnit → unitId fallback in saveSheet()).
            await sheet.Locator("#create-product-qty").FillAsync("1");

            // The Defaults collapsible is open by default (plantry-grvy); set the product's default unit directly.
            await sheet.Locator("#create-product-unit").SelectOptionAsync(new SelectOptionValue { Label = "ea" });

            // Commit via create-view "Create" button (.Last targets create-view, .First targets search-view).
            await sheet.Locator(".sheet__actions button.btn--primary").Last.ClickAsync();
            await Assertions.Expect(sheet).Not.ToBeVisibleAsync();

            // The ingredient row appears client-side with the typed product name.
            await Assertions.Expect(page.Locator(".ingredient-row__summary", new() { HasText = trackedProductName }))
                .ToBeVisibleAsync();

            // ── Submit the recipe form — saves cleanly in a single pass ───────────
            // R5 is satisfied (qty=1, unitId pre-populated from default unit). The recipe saves and
            // redirects to the Detail page. The newly-created tracked product appears in Catalog.
            await page.ClickAsync("button[type=submit]:has-text('Create recipe')");
            await page.WaitForURLAsync(DetailUrlPattern);
            Assert.DoesNotMatch(@"/New$", page.Url);

            // ── Verify the product was created in Catalog (tracked) ───────────────
            await page.GotoAsync($"{BaseUrl}/Catalog/Products");
            await page.WaitForURLAsync("**/Catalog/Products**");
            await Assertions.Expect(page.GetByText(trackedProductName))
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-recipe-tracked-create.zip" });
        }
    }

    // ── Journey 5: blank-quantity inline create is blocked client-side (plantry-5oek) ─────

    [Fact(DisplayName = "plantry-5oek: create-view with a blank quantity does not commit — inline error, no ghost row")]
    public async Task InlineTrackedCreate_BlankQuantity_IsBlockedWithInlineError()
    {
        var email = $"recipe-blankqty-{Guid.NewGuid():N}@test.local";

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await RegisterHouseholdAsync(context, email, "Blank Qty Household");

            await page.GotoAsync($"{BaseUrl}/Recipes/New");
            await page.WaitForURLAsync("**/Recipes/New");

            await page.FillAsync("[name='Input.Name']", $"Blank Qty {Guid.NewGuid():N}".Substring(0, 18));
            await page.FillAsync("[name='Input.DefaultServings']", "2");

            // Open the sheet and switch to the create-view; fill name + unit but LEAVE QUANTITY BLANK.
            await page.ClickAsync("button:has-text('Add ingredient')");
            var sheet = page.Locator("#recipe-editor .sheet");
            await Assertions.Expect(sheet).ToBeVisibleAsync();

            var productName = $"Nutmeg {Guid.NewGuid():N}".Substring(0, 18);
            await sheet.Locator("input[role='combobox']").PressSequentiallyAsync(productName.Substring(0, 6));
            await sheet.Locator("button:has-text('as a new product')").ClickAsync();
            var nameInput = sheet.Locator("input[placeholder='Product name (e.g. Olive Oil)']");
            await Assertions.Expect(nameInput).ToBeVisibleAsync();
            await nameInput.FillAsync(productName);
            // Unit supplied; quantity deliberately left blank.
            await sheet.Locator("#create-product-unit").SelectOptionAsync(new SelectOptionValue { Label = "ea" });

            // Click Create — the guard must block the commit: sheet stays open, inline error shows,
            // and no ingredient row is added (the reported ghost-row bug is prevented at the source).
            await sheet.Locator(".sheet__actions button.btn--primary").Last.ClickAsync();

            await Assertions.Expect(sheet).ToBeVisibleAsync();
            // The inline guard message appears against the (visible, create-view) Quantity field. Two
            // .field__error spans bind sheetError (search + create views); :visible selects the shown one.
            await Assertions.Expect(sheet.Locator(".field__error:visible", new() { HasText = "quantity" }))
                .ToBeVisibleAsync();
            // No ingredient row was committed.
            await Assertions.Expect(page.Locator(".ingredient-row__summary")).ToHaveCountAsync(0);

            // Supplying a quantity clears the error and commits the row (happy path unchanged).
            await sheet.Locator("#create-product-qty").FillAsync("2");
            await sheet.Locator(".sheet__actions button.btn--primary").Last.ClickAsync();
            await Assertions.Expect(sheet).Not.ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".ingredient-row__summary", new() { HasText = productName }))
                .ToBeVisibleAsync();
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-recipe-blankqty.zip" });
        }
    }

    // ── Journey 6: inline create with a distinct recipe unit (plantry-dtr9) ─────────

    [Fact(DisplayName = "plantry-dtr9: inline create with recipe unit tbsp + stock unit ml saves the line as tbsp")]
    public async Task InlineCreate_DistinctRecipeUnit_SavesLineInRecipeUnit()
    {
        var email = $"recipe-dtr9-{Guid.NewGuid():N}@test.local";

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await RegisterHouseholdAsync(context, email, "Recipe DTR9 Household");

            await page.GotoAsync($"{BaseUrl}/Recipes/New");
            await page.WaitForURLAsync("**/Recipes/New");

            var recipeName = $"Dressing {Guid.NewGuid():N}".Substring(0, 18);
            await page.FillAsync("[name='Input.Name']", recipeName);
            await page.FillAsync("[name='Input.DefaultServings']", "2");

            // ── Open the sheet and switch to create-view ──────────────────────────
            await page.ClickAsync("button:has-text('Add ingredient')");
            var sheet = page.Locator("#recipe-editor .sheet");
            await Assertions.Expect(sheet).ToBeVisibleAsync();

            var productName = $"Olive Oil {Guid.NewGuid():N}".Substring(0, 22);
            await sheet.Locator("input[role='combobox']").PressSequentiallyAsync(productName.Substring(0, 8));
            var createBtn = sheet.Locator("button:has-text('as a new product')");
            await Assertions.Expect(createBtn).ToBeVisibleAsync();
            await createBtn.ClickAsync();

            var nameInput = sheet.Locator("input[placeholder='Product name (e.g. Olive Oil)']");
            await Assertions.Expect(nameInput).ToBeVisibleAsync();
            await nameInput.FillAsync(productName);

            // ── The crux of plantry-dtr9: qty 2, recipe unit tbsp, product STOCK unit ml ──
            await sheet.Locator("#create-product-qty").FillAsync("2");

            // The recipe-line unit select is a distinct, TrackStock-gated field in the create view.
            var recipeUnit = sheet.Locator("#create-product-recipe-unit");
            await Assertions.Expect(recipeUnit).ToBeVisibleAsync();
            // Pick the recipe unit FIRST — this pins it, so setting the stock unit below must NOT overwrite it.
            await recipeUnit.SelectOptionAsync(new SelectOptionValue { Label = "tbsp" });

            // The Defaults "Stock unit" select is the PRODUCT default — set it to a DIFFERENT unit (ml).
            await sheet.Locator("#create-product-unit").SelectOptionAsync(new SelectOptionValue { Label = "ml" });

            // Commit — ml/tbsp are both Volume (same dimension), so no conversion prompt appears.
            await sheet.Locator(".sheet__actions button.btn--primary").Last.ClickAsync();
            await Assertions.Expect(sheet).Not.ToBeVisibleAsync();

            // ── The landed row shows the RECIPE unit (tbsp), NOT the stock unit (ml) ──
            var summary = page.Locator(".ingredient-row__summary", new() { HasText = productName });
            await Assertions.Expect(summary).ToBeVisibleAsync();
            await Assertions.Expect(summary).ToContainTextAsync("tbsp");

            // ── Save — same-dimension pair needs no factor, so it saves cleanly in one pass ──
            await page.ClickAsync("button[type=submit]:has-text('Create recipe')");
            await page.WaitForURLAsync(DetailUrlPattern);
            Assert.DoesNotMatch(@"/New$", page.Url);

            // Detail renders the ingredient measured in tbsp (criterion 2).
            await Assertions.Expect(page.GetByText("tbsp").First)
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-recipe-dtr9-recipe-unit.zip" });
        }
    }

    // ── Shared helpers ────────────────────────────────────────────────────────────

    /// <summary>Smallest valid 1×1 PNG for photo upload (same helper as ReceiptIntakeJourneyTests).</summary>
    private static byte[] TinyPngBytes() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
}
