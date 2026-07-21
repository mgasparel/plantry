using Microsoft.Playwright;
using Npgsql;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E — the recipe-editor conversion prompt's MISSING-DEFAULT branch renders its explicit,
/// actionable message at runtime and NEVER mounts the four-field equation (plantry-tg79, closing the
/// Gate-6 runtime gap left by plantry-obg3's AC3).
///
/// plantry-obg3 added a <c>defaultUnitMissing</c> flag so that when a product's stock/default unit is
/// unset or dangles (no resolvable code), the conversion prompt shows "…has no stock unit set…" instead
/// of a blank-unit sentence + option-less dropdown. The server flag emission is unit-tested
/// (RecipeEditorCheckConversionTests) and the guarded markup is snapshot-tested, but the client-runtime
/// Alpine toggle — <c>x-if="draft.defaultUnitMissing"</c> in the in-sheet prompt and
/// <c>x-show="row.defaultUnitMissing"</c> on the landed row — was never driven end-to-end. Only a real
/// browser proves the equation is genuinely absent from the DOM (x-if removes it, not just CSS-hides it)
/// and that the message is what actually renders.
///
/// The unresolvable-default state has no UI surface (the product-create form requires a valid unit), so
/// the test forces it exactly as the dogfooded olive-oil defect arose: seed a valid product through the
/// UI, then make <c>catalog.products.default_unit_id</c> dangling via a direct SQL UPDATE on the owner
/// connection (<c>AppHostFixture.DbConnectionString</c>). The column carries no FK to the units table, so
/// the update is safe and mirrors the server unit test's MissingDefaultUnitId fixture.
///
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class RecipeConversionMissingDefaultJourneyTests(AppHostFixture appHost) : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    private string BaseUrl => appHost.BaseUrl;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        // The landed-row block hydrates via an async x-init fetch (maybeHydrateRowConversion), so give
        // web-first assertions room to poll across the async populate rather than tripping the 5s default
        // on a cold Aspire stack — matching RecipeConversionToneJourneyTests.
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
    /// Seeds one catalog product whose DEFAULT unit is <paramref name="unitLabel"/> and returns nothing.
    /// The product-create select renders options as "{code} — {name}" (e.g. "g — gram"). A valid unit is
    /// required at creation; the dangling state is forced afterwards via <see cref="MakeDefaultUnitDanglingAsync"/>.
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

    /// <summary>Resolves a seeded product's id by name via the owner connection (not subject to RLS).</summary>
    private async Task<Guid> GetProductIdAsync(string productName)
    {
        await using var conn = new NpgsqlConnection(appHost.DbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id FROM catalog.products WHERE name = @n LIMIT 1", conn);
        cmd.Parameters.AddWithValue("@n", productName);
        var id = await cmd.ExecuteScalarAsync() as Guid?;
        Assert.True(id.HasValue, $"Product '{productName}' not found in catalog.");
        return id!.Value;
    }

    /// <summary>
    /// Forces the unresolvable-default state: points <c>catalog.products.default_unit_id</c> at a random
    /// GUID absent from the household unit list. The column has NO foreign key to the units table
    /// (CatalogDbContext configures no relationship), so this is a safe direct UPDATE and creates exactly
    /// the dangling condition the server unit test forces with RecipeEditorFixture.MissingDefaultUnitId.
    /// Runs on the owner connection, so it is not blocked by RLS.
    /// </summary>
    private async Task MakeDefaultUnitDanglingAsync(Guid productId)
    {
        await using var conn = new NpgsqlConnection(appHost.DbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE catalog.products SET default_unit_id = @dangling WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@dangling", Guid.NewGuid());
        cmd.Parameters.AddWithValue("@id", productId);
        var affected = await cmd.ExecuteNonQueryAsync();
        Assert.Equal(1, affected);
    }

    /// <summary>Commits the staged sheet row via the search-view Add button and waits for the sheet to close.</summary>
    private static async Task CommitSheetAsync(ILocator sheet)
    {
        // Two .sheet__actions bars exist (search + create views); .First targets the search-view Add.
        await sheet.Locator(".sheet__actions button.btn--primary").First.ClickAsync();
        await Assertions.Expect(sheet).Not.ToBeVisibleAsync();
    }

    // ── Journey: missing default → explicit message, equation never mounts ─────────

    [Fact(DisplayName = "Missing default unit: conversion check renders the explicit message, never the equation (sheet + landed row)")]
    public async Task MissingDefaultUnit_ConversionCheck_RendersMessage_NeverEquation()
    {
        var email = $"recipe-conv-missing-{Guid.NewGuid():N}@test.local";

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await RegisterHouseholdAsync(context, email, "Recipe Conv Missing Household");

            // Seed a valid mass-default product, then break its default unit so it dangles — the
            // dogfooded olive-oil condition. No AI-assist toggle is needed: the prompt is driven by the
            // client $watch → CheckConversion path, independent of the AI-assistance setting.
            var productName = $"Olive Oil {Guid.NewGuid():N}".Substring(0, 20);
            await SeedProductAsync(page, productName, unitLabel: "g — gram");
            var productId = await GetProductIdAsync(productName);
            await MakeDefaultUnitDanglingAsync(productId);

            await page.GotoAsync($"{BaseUrl}/Recipes/New");
            await page.WaitForURLAsync("**/Recipes/New");
            await page.FillAsync("[name='Input.Name']", $"Vinaigrette {Guid.NewGuid():N}".Substring(0, 20));
            await page.FillAsync("[name='Input.DefaultServings']", "2");

            // ── Stage the ingredient (inlined variant of the tone suite's StageCrossDimensionIngredient) ──
            // We deliberately do NOT assert the Stock-amount input becomes visible — with a dangling
            // default the equation must never mount, which is the opposite of that helper's final step.
            await page.ClickAsync("button:has-text('Add ingredient')");
            var sheet = page.Locator("#recipe-editor .sheet");
            await Assertions.Expect(sheet).ToBeVisibleAsync();

            // Type char-by-char so the htmx "keyup" search fires (FillAsync sets .value without keyup).
            await sheet.Locator("input[role='combobox']").PressSequentiallyAsync(productName.Substring(0, 8));
            var option = sheet.Locator(".searchable-select__listbox li[role='option']", new() { HasText = productName });
            await Assertions.Expect(option).ToBeVisibleAsync();
            await option.ClickAsync();

            // Product picked → the dangling default resolves to nothing, so no prompt yet: exactly one
            // visible number input (Quantity) and one visible select (line unit). Pick a real recipe-line
            // unit ("cup"): its id can never equal the dangling default, so the debounced $watch fires
            // CheckConversion → server returns needsConversion:true, defaultUnitMissing:true.
            await sheet.Locator("input[type='number']:visible").FillAsync("2");
            await sheet.Locator("select:visible").SelectOptionAsync(new SelectOptionValue { Label = "cup" });

            // ── AC2: the explicit missing-default message is VISIBLE in the sheet ──────────────────────
            // Copy owned by plantry-hhy2 (stock-axis branch) — if hhy2 rewords it, update this substring
            // only; the structural assertions below (message container visible, .conv-eq absent) are stable.
            var sheetMsg = sheet.Locator("p", new() { HasText = "has no stock unit set" });
            await Assertions.Expect(sheetMsg).ToBeVisibleAsync();
            await Assertions.Expect(sheetMsg.Locator("strong")).ToHaveTextAsync(productName);

            // ── AC3: the equation editor is genuinely ABSENT from the sheet DOM (x-if, not x-show) ──────
            await Assertions.Expect(sheet.Locator(".conv-eq")).ToHaveCountAsync(0);
            await Assertions.Expect(sheet.Locator("input[aria-label='Stock amount']")).ToHaveCountAsync(0);
            await Assertions.Expect(sheet.Locator("select[aria-label='Stock unit']")).ToHaveCountAsync(0);

            // ── AC4: commit (allowed with the prompt showing) and repeat on the landed row ─────────────
            await CommitSheetAsync(sheet);

            var row = page.Locator(".ingredient-row").First;
            await Assertions.Expect(row).ToBeVisibleAsync();
            var conv = row.Locator(".ingredient-row__conversion");

            // Message visible: row.defaultUnitMissing arrives cloned from the committed draft (and is
            // re-confirmed by maybeHydrateRowConversion's x-init fetch); auto-waiting absorbs the async fetch.
            // plantry-hhy2 owns this copy — update the substring only if hhy2 rewords it.
            await Assertions.Expect(conv.GetByText("has no stock unit set")).ToBeVisibleAsync();

            // Equation absent on the landed row too (behind template x-if="!row.defaultUnitMissing …").
            await Assertions.Expect(conv.Locator(".conv-eq")).ToHaveCountAsync(0);
            await Assertions.Expect(conv.Locator("input[aria-label='Stock amount']")).ToHaveCountAsync(0);
            await Assertions.Expect(conv.Locator("select[aria-label='Stock unit']")).ToHaveCountAsync(0);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-recipe-conv-missing.zip" });
        }
    }
}
