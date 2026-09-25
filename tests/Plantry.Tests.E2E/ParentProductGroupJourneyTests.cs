using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;
using Aspire.Hosting.Testing;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E journey (Playwright) tying the whole plantry-oh27 epic together (plantry-oh27.7, the final
/// child): create a parent + two live variants → add stock to one variant → the parent's detail page
/// shows the rolled-up aggregate → setting the parent's low-stock threshold re-renders it as running
/// low → the parent (not a flavour) surfaces in the Shopping page's Pantry-suggestions strip. Each leg
/// exercises a different sibling bead's rollup (IOnHandRollupQuery, LowStockRule, and
/// ShoppingPantryReaderAdapter/PantrySuggestionService) through the one surface a household actually
/// uses them from — the parent product detail page this bead adds.
///
/// Boots the whole service graph from the Aspire AppHost via AppHostFixture, mirroring
/// <see cref="StockSmokeTests"/>'s shape.
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class ParentProductGroupJourneyTests(AppHostFixture appHost) : IAsyncLifetime
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

    [Fact(DisplayName = "Parent group: aggregate on-hand → low-stock alert → Shopping pantry suggestion")]
    public async Task ParentAggregateThresholdAndSuggestion()
    {
        var uniqueEmail = $"parentgroup-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";
        // Distinct enough to survive Shopping's suggestion cap/dedup without colliding with any other
        // seeded/parallel household's rows — this test creates its own household, but the name itself
        // must still be locatable via a simple substring match on the rendered page.
        var parentName = $"Bubly {Guid.NewGuid():N}".Substring(0, 22);

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── Register a household (lands on Today home, logged in) ─────────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", "Parent Group Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "Parent Group User");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            // ── Create the standalone product that becomes the parent ───────────
            await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
            await page.WaitForURLAsync("**/Catalog/Products/Create");
            await page.FillAsync("[name='Input.Name']", parentName);
            await page.SelectOptionAsync("[name='Input.DefaultUnitId']", new SelectOptionValue { Label = "g — gram" });
            await page.ClickAsync("button:has-text('Create Product')");
            await page.WaitForURLAsync("**/Catalog/Products/**");
            var parentUrl = page.Url; // {BaseUrl}/Catalog/Products/{parentId} — reused for the 2nd variant

            // ── Add the first variant (this submission also turns the product into a parent —
            //    Detail.cshtml.cs OnPostAddVariantAsync / CreateVariantCommand) ───
            await page.FillAsync("[name='AddVariantInput.Name']", "Lime");
            await page.ClickAsync("button:has-text('Add variant')");
            await page.WaitForURLAsync("**/Catalog/Products/**"); // redirected to the new variant's own page
            var variant1Url = page.Url;

            // ── Add a second variant, from the PARENT's page (not the first variant's) ──
            await page.GotoAsync(parentUrl);
            await page.FillAsync("[name='AddVariantInput.Name']", "Grapefruit");
            await page.ClickAsync("button:has-text('Add variant')");
            await page.WaitForURLAsync("**/Catalog/Products/**");

            // ── Add stock to the first variant only — the parent's aggregate must still reflect it ──
            await page.GotoAsync(variant1Url);
            var variant1Id = new Uri(variant1Url).Segments[^1];
            await page.GotoAsync($"{BaseUrl}/Pantry/Products/Detail/{variant1Id}");
            await page.ClickAsync("button:has-text('Add stock')"); // zero-stock landing's primary CTA
            await Assertions.Expect(page.Locator("#sheet-host .sheet__panel")).ToBeVisibleAsync();
            await page.FillAsync("[name='AddStockInput.Quantity']", "500");
            await page.SelectOptionAsync("[name='AddStockInput.UnitId']", new SelectOptionValue { Label = "g — gram" });
            await page.SelectOptionAsync("[name='AddStockInput.LocationId']", new SelectOptionValue { Label = "Pantry" });
            await page.ClickAsync("button:has-text('Add to pantry')");
            await Assertions.Expect(page.Locator("#product-total")).ToContainTextAsync("500g");

            // ── Open the PARENT's own pantry detail page — aggregate on-hand rolls up the variant ──
            var parentId = new Uri(parentUrl).Segments[^1];
            await page.GotoAsync($"{BaseUrl}/Pantry/Products/Detail/{parentId}");
            await Assertions.Expect(page.Locator("#product-total")).ToContainTextAsync("500g");
            await Assertions.Expect(page.Locator("body")).ToContainTextAsync("Lime");
            await Assertions.Expect(page.Locator("body")).ToContainTextAsync("Grapefruit");

            // ── Set the parent's low-stock threshold above the aggregate — re-renders as running low ──
            await page.ClickAsync("a:has-text('Set alert')");
            await Assertions.Expect(page.Locator("#sheet-host .sheet__panel")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#sheet-host")).ToContainTextAsync("across all variants");
            await page.FillAsync("[name='ThresholdInput.Threshold']", "1000");
            await page.ClickAsync("button:has-text('Save')");
            await Assertions.Expect(page.Locator("#product-total")).ToContainTextAsync("Running low");

            // ── Shopping's Pantry-suggestions strip surfaces the PARENT (Tier 1, threshold breach) ──
            await page.GotoAsync($"{BaseUrl}/Shopping");
            await Assertions.Expect(page.Locator("#pantry-suggestions")).ToContainTextAsync(parentName);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-parent-group.zip" });
        }
    }
}
