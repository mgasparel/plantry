using Microsoft.Playwright;
using Npgsql;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E smoke test for the Phase-5 <b>deal-review</b> banner on the Today (Home) page
/// (plantry-bpw / DJ4 / SPEC §0b acceptance criteria):
///
///   "With pending in-window deals the banner shows and deep-links to the P5-8 review queue; after
///    clearing the queue it disappears; an all-expired pending set shows NO banner (the count is
///    recomputed via BrowseDeals against the clock, never the stamped FlyerImported.pendingCount)."
///
/// Deals normally arrive through the P5-6 ingest worker (subscribe → pull flyer → match → materialize),
/// which the deterministic stub flyer source does not drive in the E2E stack. So this test seeds the
/// <c>deals.deal</c> rows directly over the AppHost's owner connection (not subject to RLS), then drives
/// the real Razor page + <c>BrowseDeals</c> read service through the browser. The deal windows are set
/// relative to <b>today</b> (the app's SystemClock, in UTC) — an in-window deal to raise the banner, then
/// an expired window to prove the count is recomputed against the clock (DD14), not a stamped snapshot.
///
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class TodayDealReviewBannerSmokeTests(AppHostFixture appHost) : IAsyncLifetime
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

    [Fact(DisplayName = "Today: pending in-window deals show a deal-review banner deep-linking to the queue; it clears when the queue empties and never shows for expired deals (plantry-bpw/AC)")]
    public async Task Today_DealReviewBanner_ShowsDeepLinksClearsAndHidesWhenExpired()
    {
        var householdName = $"Deal Banner HH {Guid.NewGuid():N}";
        var email = $"dealbanner-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── 1. Register a fresh household (lands on Today, logged in) ──────────
            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", householdName);
            await page.FillAsync("[name='Input.Email']", email);
            await page.FillAsync("[name='Input.DisplayName']", "Deal Banner Tester");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            var householdId = await GetHouseholdIdAsync(householdName);
            var dealId = Guid.NewGuid();
            var today = DateOnly.FromDateTime(DateTime.UtcNow);

            // A freshly-registered household is in cold-start (no stock/recipes/intake), which the Today
            // page renders as a Welcome hero with NO banner stack. Seed one recipe so IsColdStart=false and
            // the normal dashboard (with the banner stack) renders — mirrors a household that has done setup
            // and subscribed to stores before deals arrive.
            await SeedRecipeAsync(householdId);

            // ── 2. Seed ONE Pending, in-window deal (valid_to in the future) ──────
            await InsertPendingDealAsync(dealId, householdId, today.AddDays(-1), today.AddDays(6));

            // ── 3. Today shows the deal-review banner deep-linking to /Deals/Review ─
            await page.GotoAsync($"{BaseUrl}/Today");
            await page.Locator(".today-wrap").WaitForAsync();

            var dealBanner = page.Locator(".today-banner--deal");
            await Assertions.Expect(dealBanner).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

            var title = dealBanner.Locator(".today-banner__title");
            var titleText = await title.TextContentAsync();
            Assert.NotNull(titleText);
            Assert.Contains("review", titleText, StringComparison.OrdinalIgnoreCase);

            var reviewLink = dealBanner.Locator(".today-banner__review");
            await Assertions.Expect(reviewLink).ToBeVisibleAsync();
            var href = await reviewLink.GetAttributeAsync("href");
            Assert.Equal("/Deals/Review", href);

            // Click Review → navigates into the P5-8 review queue.
            await reviewLink.ClickAsync();
            await page.WaitForURLAsync("**/Deals/Review**");

            // ── 4. Clear the queue (deal reviewed → no longer Pending) → banner gone ─
            await SetDealStatusAsync(dealId, "rejected");
            await page.GotoAsync($"{BaseUrl}/Today");
            await page.Locator(".today-wrap").WaitForAsync();
            await Assertions.Expect(page.Locator(".today-banner--deal")).ToHaveCountAsync(0);

            // ── 5. All-expired Pending set → still NO banner (clock-driven recount) ─
            await MakePendingButExpiredAsync(dealId, today.AddDays(-6), today.AddDays(-1));
            await page.GotoAsync($"{BaseUrl}/Today");
            await page.Locator(".today-wrap").WaitForAsync();
            await Assertions.Expect(page.Locator(".today-banner--deal")).ToHaveCountAsync(0);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-today-deal-review-banner.zip" });
        }
    }

    /// <summary>Resolves the registered household's id by its unique name (owner connection, no RLS).</summary>
    private async Task<Guid> GetHouseholdIdAsync(string householdName)
    {
        await using var conn = new NpgsqlConnection(appHost.DbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id FROM identity.households WHERE name = @n ORDER BY created_at DESC LIMIT 1", conn);
        cmd.Parameters.AddWithValue("@n", householdName);
        var result = await cmd.ExecuteScalarAsync();
        Assert.NotNull(result);
        return (Guid)result!;
    }

    /// <summary>Seeds a minimal recipe so the household is no longer cold-start (banner stack renders).</summary>
    private async Task SeedRecipeAsync(Guid householdId)
    {
        await using var conn = new NpgsqlConnection(appHost.DbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO recipes.recipe (recipe_id, household_id, name, default_servings, created_at, updated_at)
            VALUES (@id, @hh, 'E2E Seed Recipe', 2, now(), now())
            """, conn);
        cmd.Parameters.AddWithValue("@id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("@hh", householdId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Seeds one Pending flyer deal for the household with the given validity window.</summary>
    private async Task InsertPendingDealAsync(Guid dealId, Guid householdId, DateOnly validFrom, DateOnly validTo)
    {
        await using var conn = new NpgsqlConnection(appHost.DbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO deals.deal
              (deal_id, household_id, flyer_import_id, store_id, source, raw_name, price, normalized_name,
               match_confidence, status, valid_from, valid_to, auto_matched, created_at, updated_at)
            VALUES
              (@id, @hh, NULL, @store, 'flyer', 'Fresh Salmon', 4.99, 'fresh salmon',
               'none', 'pending', @from, @to, false, now(), now())
            """, conn);
        cmd.Parameters.AddWithValue("@id", dealId);
        cmd.Parameters.AddWithValue("@hh", householdId);
        cmd.Parameters.AddWithValue("@store", Guid.NewGuid());
        cmd.Parameters.AddWithValue("@from", validFrom);
        cmd.Parameters.AddWithValue("@to", validTo);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Marks the deal as reviewed (out of the Pending queue) — simulates the queue being cleared.</summary>
    private async Task SetDealStatusAsync(Guid dealId, string status)
    {
        await using var conn = new NpgsqlConnection(appHost.DbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE deals.deal SET status = @s, updated_at = now() WHERE deal_id = @id", conn);
        cmd.Parameters.AddWithValue("@s", status);
        cmd.Parameters.AddWithValue("@id", dealId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Returns the deal to Pending but with a closed window (valid_to in the past).</summary>
    private async Task MakePendingButExpiredAsync(Guid dealId, DateOnly validFrom, DateOnly validTo)
    {
        await using var conn = new NpgsqlConnection(appHost.DbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE deals.deal SET status = 'pending', valid_from = @from, valid_to = @to, updated_at = now() WHERE deal_id = @id",
            conn);
        cmd.Parameters.AddWithValue("@from", validFrom);
        cmd.Parameters.AddWithValue("@to", validTo);
        cmd.Parameters.AddWithValue("@id", dealId);
        await cmd.ExecuteNonQueryAsync();
    }
}
