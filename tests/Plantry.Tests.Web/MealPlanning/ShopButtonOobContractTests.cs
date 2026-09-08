using Microsoft.AspNetCore.Mvc.Testing;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.MealPlanning;

/// <summary>
/// Regression coverage for plantry-pl5u: the "Shop for this week" button posted the initial
/// page-load week even after the user navigated to a different week via the Previous/Next/This-week
/// htmx controls, because the button lived outside the swapped fragment and never re-hydrated its
/// Alpine <c>weekDate</c>.
///
/// The fix moves the Shop control into its own partial (<c>_ShopButton.cshtml</c>) that is re-emitted
/// out-of-band by <c>_GridWithBarNav.cshtml</c> — the same response every Previous/Next/This-week nav
/// click swaps in — so the button's <c>x-init(week)</c> is rebuilt from the week the server actually
/// resolved for that Grid response every time. Because the OOB fragment's week comes from the exact
/// same <c>BarNav.WeekStart</c> the visible nav/grid renders from (see
/// <c>IndexModel.GridWithBarNavVm.ShopButton</c>), the Shop control and the rendered grid can never
/// disagree about which week is "current" — this is what proves the fix without needing a browser: the
/// server-rendered contract guarantees no independent client-side week-resolution path exists.
///
/// L5/browser-level proof that a real navigate-then-click sequence carries the new week end-to-end
/// into <c>ShopForWeekService.ExecuteAsync</c>, and that rapid repeated navigation cannot leave the
/// control pointing at a stale/aborted response, lives in
/// <c>Plantry.Tests.E2E.ShopForWeekWeekNavigationTests</c>.
/// </summary>
[Collection(nameof(PlanBarNavOobCollection))]
public sealed class ShopButtonOobContractTests(PlanBarNavOobFactory factory)
{
    private HttpClient CreateClient()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, WeekGridFixture.HouseholdId.ToString());
        return client;
    }

    // ── 1. Full page GET renders the Shop control inline for the initial week ─────

    [Fact(DisplayName = "GET /MealPlan renders plan-shop-btn inline with the initial week's ISO date")]
    public async Task GetMealPlan_RendersShopButton_WithInitialWeek()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("id=\"plan-shop-btn\"", html);
        // MealPlanningTestClock.Instant is 2026-03-10 (a Tuesday) → the containing Monday is 2026-03-09.
        Assert.Contains("init(&quot;2026-03-09&quot;)", html);
        // Inline on first load (Oob=false): the Shop control carries no hx-swap-oob attribute at all.
        Assert.DoesNotContain("hx-swap-oob", html);
    }

    // ── 2. GET Grid for a navigated week re-emits plan-shop-btn OOB with THAT week ─

    [Fact(DisplayName = "GET Grid for next week re-emits plan-shop-btn OOB with the navigated week's ISO date (plantry-pl5u)")]
    public async Task GetGrid_ForNextWeek_ReemitsShopButton_WithNavigatedWeek()
    {
        var client = CreateClient();

        // A week distinct from the fixture's "current" week (2026-03-09) — a non-current viewed week,
        // matching the ticket's edge case ("Cover a non-current viewed week ... not only the
        // current-week happy path").
        var targetMonday = new DateOnly(2026, 3, 16);
        var response = await client.GetAsync($"/MealPlan?handler=Grid&week={targetMonday:yyyy-MM-dd}");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // The OOB Shop-button fragment must carry the NAVIGATED week, not the fixture's initial week.
        Assert.Contains("id=\"plan-shop-btn\"", html);
        Assert.Contains("hx-swap-oob=\"true\"", html);
        Assert.Contains("init(&quot;2026-03-16&quot;)", html);
        Assert.DoesNotContain("init(&quot;2026-03-09&quot;)", html);

        // Also carries the plan-bar-nav OOB projection, and both agree on the same week (Jun→Mar label
        // check would be redundant with PlanBarNavOobContractTests; this test's job is the Shop button).
        OobContract.AssertCarriesProjections(html, "plan-bar-nav", "plan-shop-btn");
    }

    // ── 3. GET Grid for the household's current week re-emits plan-shop-btn with that week ─

    [Fact(DisplayName = "GET Grid for the current week re-emits plan-shop-btn OOB with the current week's ISO date")]
    public async Task GetGrid_ForCurrentWeek_ReemitsShopButton_WithCurrentWeek()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/MealPlan?handler=Grid&week=2026-03-09");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("id=\"plan-shop-btn\"", html);
        Assert.Contains("hx-swap-oob=\"true\"", html);
        Assert.Contains("init(&quot;2026-03-09&quot;)", html);
    }

    // ── 4. A non-Monday query date still resolves the Shop control to the containing Monday ─

    [Fact(DisplayName = "GET Grid for a non-Monday date still re-emits plan-shop-btn normalized to that week's Monday")]
    public async Task GetGrid_ForNonMondayDate_ReemitsShopButton_NormalizedToMonday()
    {
        var client = CreateClient();

        // 2026-03-19 is the Thursday of the 2026-03-16 week.
        var response = await client.GetAsync("/MealPlan?handler=Grid&week=2026-03-19");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("id=\"plan-shop-btn\"", html);
        Assert.Contains("init(&quot;2026-03-16&quot;)", html);
    }

    // ── 5. Week-nav buttons synchronize on the shared swap target so a later click always wins ─

    [Fact(DisplayName = "plan-bar-nav's Previous/Next/This-week buttons all carry hx-sync='#plan-main-content:replace' (plantry-pl5u race guard)")]
    public async Task PlanBarNav_WeekNavButtons_CarryHxSyncReplace()
    {
        var client = CreateClient();

        // Use a week away from "this week" so the "This week" button is present too.
        var response = await client.GetAsync("/MealPlan?handler=Grid&week=2026-03-16");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("This week", html);

        var syncCount = CountOccurrences(html, "hx-sync=\"#plan-main-content:replace\"");
        // Previous + Next + This-week = 3 nav buttons, each synchronized against the same target so a
        // later click aborts an in-flight earlier one instead of letting a delayed response overwrite
        // the newer state (the multi-nav race edge case in the ticket).
        Assert.Equal(3, syncCount);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
