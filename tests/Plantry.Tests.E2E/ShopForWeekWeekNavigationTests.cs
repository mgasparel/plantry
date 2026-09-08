using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Npgsql;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E regression coverage for plantry-pl5u: the "Shop for this week" button posted the initial
/// page-load week even after the user navigated to a different week via the visible
/// Previous/Next/This-week htmx controls, because the button's Alpine <c>weekDate</c> was set once on
/// full page load and never re-hydrated on a partial (#plan-main-content-only) swap.
///
/// These tests exercise the ACTUAL browser navigation path (clicking the real htmx nav buttons, not
/// a direct API call) and observe two independent, code-external proofs that the fix works:
///   1. The exact "week" form field the browser posts to <c>/MealPlan?handler=Shop</c>, captured via
///      network-request interception — this is the literal value <c>ShopForWeekService.ExecuteAsync</c>
///      receives.
///   2. The resulting shopping-list side effect: a product only planned in the navigated-to week
///      appears on the shopping list (and only once, source=meal_plan) after Shop is clicked — this
///      could not happen if the server actually shopped the initial week, which has nothing planned.
///
/// A second test forces a genuine rapid double-navigation race (a Grid request for an abandoned week
/// is held in flight via Playwright route interception, then a second navigation to a different week
/// fires while it is still pending) and asserts, directly, that htmx's own <c>htmx:sendAbort</c> event
/// fires — the event htmx's <c>xhr.onabort</c> handler dispatches only when a real in-flight XHR was
/// actually cancelled, i.e. the conditional, falsifiable signal that the
/// <c>hx-sync="#plan-main-content:replace"</c> guard added by this fix tore the abandoned request down
/// client-side (unlike the unconditional <c>htmx:abort</c> event the "replace" strategy always
/// dispatches regardless of whether anything was actually in flight to abort) — before also confirming
/// the Shop control and nav settle on the newer week, never the abandoned one.
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class ShopForWeekWeekNavigationTests(AppHostFixture appHost) : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    private string BaseUrl => appHost.BaseUrl;

    private static readonly Regex DetailUrlPattern = new(@"/Recipes/[0-9a-fA-F-]{36}$");
    // The DOM attribute value comes back already HTML-entity-decoded (Playwright reads the parsed
    // DOM, not the raw response bytes), so the JSON-serialized week arrives as init("...") with a
    // literal double quote — not the &quot; entity the raw HTML source contains.
    private static readonly Regex WeekInitPattern = new(@"init\(\""(\d{4}-\d{2}-\d{2})\""\)");

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

    [Fact(DisplayName = "Shop-for-week: navigating to next week via the real Next-week button, then Shop, posts the NAVIGATED week and shops only that week's meal (plantry-pl5u)")]
    public async Task ShopForWeek_AfterNavigatingToNextWeek_PostsAndShopsNavigatedWeek()
    {
        var uniqueEmail = $"navshop-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";
        var productName = $"Nav Butter {Guid.NewGuid():N}"[..22];
        var recipeName = $"Nav Cake {Guid.NewGuid():N}"[..18];

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            // ── Register + seed a product/recipe exactly like ShopForWeekSmokeTests ───
            await RegisterHouseholdAsync(page, uniqueEmail, password);
            await CreateMissingProductAsync(page, productName);
            var recipeId = await CreateRecipeWithIngredientAsync(page, recipeName, productName);

            // ── Load Meal Plan for the INITIAL week (week A) ───────────────────────
            await page.GotoAsync($"{BaseUrl}/MealPlan");
            await page.WaitForURLAsync("**/MealPlan**");
            await Assertions.Expect(page.Locator(".wkgrid")).ToBeVisibleAsync();

            var weekA = await ReadShopButtonWeekAsync(page);

            // Baseline: week A has nothing planned. Shopping it now must add nothing — this proves
            // the control below (shopping week B) really is due to the navigated week, not a
            // coincidence of ShopForWeekService always finding the ingredient regardless of week.
            var shopBtn = page.Locator("#plan-shop-btn button");
            await Assertions.Expect(shopBtn).ToBeEnabledAsync();
            var (baselineStatus, baselineWeek) = await ClickShopAndCaptureRequestAsync(page, shopBtn);
            Assert.Equal(200, baselineStatus);
            Assert.Equal(weekA, baselineWeek);
            await Assertions.Expect(shopBtn).ToContainTextAsync("Nothing missing");

            // ── Navigate to the NEXT week (week B) via the real htmx Next-week button ─
            var nextBtn = page.Locator("button[aria-label='Next week']");
            await page.RunAndWaitForResponseAsync(
                () => nextBtn.ClickAsync(),
                resp => resp.Url.Contains("handler=Grid") && resp.Status == 200);

            var weekB = await ReadShopButtonWeekAsync(page, weekA);
            Assert.NotEqual(weekA, weekB);
            Assert.Equal(DateOnly.FromDateTime(DateTime.Parse(weekA)).AddDays(7).ToString("yyyy-MM-dd"), weekB);

            // The Shop control was re-hydrated fresh by the OOB swap: done=false again, enabled.
            var shopBtnAfterNav = page.Locator("#plan-shop-btn button");
            await Assertions.Expect(shopBtnAfterNav).ToBeEnabledAsync();

            // ── Assign the recipe into an empty cell of the NOW-DISPLAYED week B ──────
            var onclick = await page.Locator(".empty-add").First.GetAttributeAsync("onclick");
            Assert.NotNull(onclick);
            var cellMatch = Regex.Match(onclick!, @"openEditor\('([^']+)',\s*'([^']+)',\s*null\)");
            Assert.True(cellMatch.Success, $"Could not parse openEditor from onclick: {onclick}");
            var cellDate = cellMatch.Groups[1].Value;
            var slotId = cellMatch.Groups[2].Value;

            // Sanity: the empty cell we're assigning into really is within week B, not week A.
            var cellDateOnly = DateOnly.Parse(cellDate);
            var weekBStart = DateOnly.Parse(weekB);
            Assert.True(cellDateOnly >= weekBStart && cellDateOnly <= weekBStart.AddDays(6),
                $"Expected empty cell date {cellDate} to fall within week B ({weekB}..{weekBStart.AddDays(6)}).");

            var token = await GetAntiforgeryTokenAsync(page);
            var assignUrl = $"{BaseUrl}/MealPlan?handler=AssignJson";
            var assignStatus = await page.EvaluateAsync<int>(@"
                async (args) => {
                    const body = JSON.stringify({
                        mode: 'dishes',
                        dishes: [{ kind: 'recipe', itemId: args.recipeId, servings: 2 }],
                        att: null,
                        attendeesOverridden: false,
                        mealId: null,
                        date: args.date,
                        slotId: args.slotId
                    });
                    const r = await fetch(args.url, {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json',
                            'RequestVerificationToken': args.token,
                            'X-Requested-With': 'XMLHttpRequest'
                        },
                        body
                    });
                    return r.status;
                }", new { url = assignUrl, recipeId, date = cellDate, slotId, token });
            Assert.Equal(200, assignStatus);

            // ── Click Shop WITHOUT reloading — the OOB-rehydrated control must post week B ──
            var (shopStatus, postedWeek) = await ClickShopAndCaptureRequestAsync(page, shopBtnAfterNav);
            Assert.Equal(200, shopStatus);
            // Direct, code-external proof: the literal "week" form field the browser sent to
            // /MealPlan?handler=Shop (which ShopForWeekService.ExecuteAsync receives verbatim,
            // per Index.cshtml.cs OnPostShopAsync) is week B, not the initial week A.
            Assert.Equal(weekB, postedWeek);
            Assert.NotEqual(weekA, postedWeek);

            var btnText = await shopBtnAfterNav.TextContentAsync();
            Assert.True(
                btnText!.Contains("item added") || btnText.Contains("items added"),
                $"Expected 'item(s) added' after shopping week B (which has the planned recipe), got: '{btnText}'");

            // ── Behavioural proof: the product now appears on the shopping list ────────
            await page.GotoAsync($"{BaseUrl}/Shopping");
            await page.WaitForURLAsync("**/Shopping**");
            await Assertions.Expect(page.Locator("#shopping-list")).ToContainTextAsync(productName);

            var itemCount = await page.Locator(".sl-item", new() { HasText = productName }).CountAsync();
            Assert.Equal(1, itemCount);

            var productId = await GetProductIdAsync(productName);
            Assert.True(productId.HasValue, $"Product '{productName}' not found in catalog.");
            Assert.True(await ShoppingItemHasMealPlanSourceAsync(productId!.Value),
                $"Expected shopping list item for '{productName}' to have source='meal_plan'.");
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-shop-for-week-nav.zip" });
        }
    }

    [Fact(DisplayName = "Shop-for-week: rapid double week-navigation leaves the Shop control on the FINAL week even when the abandoned week's response arrives late/out-of-order (plantry-pl5u race guard)")]
    public async Task ShopForWeek_AfterRapidDoubleNavigation_NeverAppliesStaleOutOfOrderResponse()
    {
        var uniqueEmail = $"racenav-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            await RegisterHouseholdAsync(page, uniqueEmail, password);

            await page.GotoAsync($"{BaseUrl}/MealPlan");
            await page.WaitForURLAsync("**/MealPlan**");
            await Assertions.Expect(page.Locator(".wkgrid")).ToBeVisibleAsync();

            var weekA = await ReadShopButtonWeekAsync(page);
            // Next and Previous are independent buttons whose hx-get URLs are both derived from the
            // server-rendered week A state and therefore stay correct (weekB, weekP) EVEN IF fired
            // back-to-back before either response lands — unlike double-clicking the SAME button,
            // whose second click would still target the stale, not-yet-updated href. This is what
            // lets the test force two requests for two genuinely different weeks without needing the
            // DOM to update between clicks.
            var weekB = DateOnly.Parse(weekA).AddDays(7).ToString("yyyy-MM-dd");  // Next  — abandoned
            var weekP = DateOnly.Parse(weekA).AddDays(-7).ToString("yyyy-MM-dd"); // Previous — wins

            // hx-sync="#plan-main-content:replace" dispatches htmx's "htmx:abort" custom event on the
            // sync-scope element UNCONDITIONALLY whenever a request declaring that sync starts — in
            // the vendored htmx (wwwroot/js/htmx.min.js), the "replace" branch is a bare
            // `he(h,"htmx:abort")` with no in-flight guard, so that event fires even when nothing is
            // actually in flight to abort. It is therefore NOT proof that a real XHR was torn down —
            // listening for it would make this assertion pass even if htmx's "replace" teardown were
            // completely broken, since deleting hx-sync removes the (unconditional) event too. The
            // genuine, conditional signal is "htmx:sendAbort", which htmx only fires from the
            // request's own `xhr.onabort` handler — i.e. strictly when a real in-flight request was
            // actually cancelled. It is dispatched on the REQUEST'S OWN TRIGGERING ELEMENT (the Next
            // button, which lives in #plan-bar-nav, a sibling of #plan-main-content — see
            // Index.cshtml), not on the sync-scope element, so the listener must live on `document`
            // to catch it regardless of which button ends up torn down.
            //
            // A DOM-state assertion alone cannot serve as this proof either: whichever response lands
            // last, in either click order, ends up correctly determining the final week ANYWAY —
            // htmx separately (and correctly) discards a response whose triggering element is no
            // longer connected once ANY other response has replaced the shared <nav id="plan-bar-nav">
            // fragment. That means the "final DOM state is right" property is guaranteed by that
            // unrelated built-in behavior alone, hardware-timing-dependent for which response happens
            // to land first — asserting only on it would go green whether or not hx-sync exists,
            // exactly the "delete the guard and it still goes green" vacuity flagged in review.
            //
            // hx-sync can only have anything to abort if week B's request is DEFINITELY still "in
            // flight" (from the renderer's own point of view) at the moment week P's click fires — on
            // a fast local dev server, an entirely un-intercepted week B can complete before two
            // sequential Playwright clicks even finish dispatching, leaving nothing to abort. Holding
            // week B behind an explicit Playwright route gate makes it provably still pending when the
            // second click fires, removing that dependency on relative network speed entirely.
            await page.EvaluateAsync(@"() => {
                window.__htmxSendAbortCount = 0;
                document.addEventListener('htmx:sendAbort', () => {
                    window.__htmxSendAbortCount++;
                });
            }");

            var weekBRoutePattern = new Regex(@"handler=Grid&week=" + Regex.Escape(weekB));
            var weekBRouteEntered = new TaskCompletionSource();
            var releaseWeekB = new TaskCompletionSource();
            var weekBSettled = new TaskCompletionSource();
            await page.RouteAsync(weekBRoutePattern, async route =>
            {
                weekBRouteEntered.TrySetResult();
                try
                {
                    await releaseWeekB.Task;
                    try { await route.ContinueAsync(); } catch { /* torn down; DOM assertions are the source of truth */ }
                }
                finally
                {
                    // Always signal settlement, even if something above throws unexpectedly, so a
                    // failed assertion elsewhere in this test can never leave this route (and the
                    // browser's still-pending request) permanently parked into teardown.
                    weekBSettled.TrySetResult();
                }
            });

            var nextBtn = page.Locator("button[aria-label='Next week']");
            var prevBtn = page.Locator("button[aria-label='Previous week']");

            try
            {
                // First click → fires the (intercepted, held) week B request — guaranteed to still be
                // "in flight" for as long as this test chooses, since it will not be forwarded to the
                // network until releaseWeekB is signalled.
                await nextBtn.ClickAsync();
                await weekBRouteEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

                // Second click, on a DIFFERENT nav button, fired while week B is definitely still
                // pending — this is the moment hx-sync's "replace" strategy tears down week B's xhr.
                await prevBtn.ClickAsync();

                // The direct, falsifiable proof: htmx's xhr.onabort fired for the still-pending
                // abandoned request, i.e. a REAL in-flight XHR was cancelled — not merely that the
                // (unconditional) htmx:abort event was dispatched. xhr.onabort runs asynchronously, so
                // this must be a poll, not a synchronous read — a timeout here means no real abort
                // ever happened and is the desired red result if hx-sync is removed or broken.
                await page.WaitForFunctionAsync(
                    "() => window.__htmxSendAbortCount > 0",
                    null,
                    new PageWaitForFunctionOptions { Timeout = 15000 });
            }
            finally
            {
                // Release week B's held route unconditionally — whether the assertion above passed,
                // failed, or the wait timed out — so a red result can never also hang teardown with a
                // route handler still parked on releaseWeekB and a request the browser awaits forever.
                releaseWeekB.TrySetResult();
            }

            await weekBSettled.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await page.WaitForFunctionAsync("() => document.querySelectorAll('.htmx-request').length === 0");

            // Secondary confirmation: the final rendered state is correct regardless of which response
            // physically landed last — the Shop control and nav both reflect week P, never week B.
            var finalWeek = await ReadShopButtonWeekAsync(page);
            Assert.Equal(weekP, finalWeek);
            Assert.NotEqual(weekB, finalWeek);

            var nextHrefAfter = await page.Locator("button[aria-label='Next week']").GetAttributeAsync("hx-get");
            Assert.NotNull(nextHrefAfter);
            Assert.Contains(DateOnly.Parse(weekP).AddDays(7).ToString("yyyy-MM-dd"), nextHrefAfter);

            // Final proof: clicking Shop now posts week P, never the abandoned week B.
            var shopBtn = page.Locator("#plan-shop-btn button");
            await Assertions.Expect(shopBtn).ToBeEnabledAsync();
            var (status, postedWeek) = await ClickShopAndCaptureRequestAsync(page, shopBtn);
            Assert.Equal(200, status);
            Assert.Equal(weekP, postedWeek);
            Assert.NotEqual(weekB, postedWeek);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-shop-for-week-race.zip" });
        }
    }

    // ── shared journey helpers (mirrors ShopForWeekSmokeTests) ──────────────────

    private async Task RegisterHouseholdAsync(IPage page, string email, string password)
    {
        await page.GotoAsync($"{BaseUrl}/Account/Register");
        await page.WaitForURLAsync("**/Account/Register");
        await page.FillAsync("[name='Input.HouseholdName']", "Nav Shop Household");
        await page.FillAsync("[name='Input.Email']", email);
        await page.FillAsync("[name='Input.DisplayName']", "Nav Shop User");
        await page.FillAsync("[name='Input.Password']", password);
        await page.ClickAsync("button[type=submit]");
        await page.WaitForURLAsync("**/Today**");
    }

    private async Task CreateMissingProductAsync(IPage page, string productName)
    {
        await page.GotoAsync($"{BaseUrl}/Catalog/Products/Create");
        await page.WaitForURLAsync("**/Catalog/Products/Create");
        await page.FillAsync("[name='Input.Name']", productName);
        await page.SelectOptionAsync("[name='Input.DefaultUnitId']", new SelectOptionValue { Label = "g — gram" });
        await page.ClickAsync("button[type=submit]:has-text('Create Product')");
        await page.WaitForURLAsync("**/Catalog/**");
    }

    private async Task<string> CreateRecipeWithIngredientAsync(IPage page, string recipeName, string productName)
    {
        await page.GotoAsync($"{BaseUrl}/Recipes/New");
        await page.WaitForURLAsync("**/Recipes/New");
        await page.FillAsync("[name='Input.Name']", recipeName);
        await page.FillAsync("[name='Input.DefaultServings']", "2");

        await page.ClickAsync("button:has-text('Add ingredient')");
        var ingSheet = page.Locator("#recipe-editor .sheet");
        await Assertions.Expect(ingSheet).ToBeVisibleAsync();
        await ingSheet.Locator("input[role='combobox']").PressSequentiallyAsync(productName[..8]);
        var ingOption = ingSheet.Locator(".searchable-select__listbox li[role='option']", new() { HasText = productName });
        await Assertions.Expect(ingOption).ToBeVisibleAsync();
        await ingOption.ClickAsync();
        await ingSheet.Locator("input[type='number']:visible").FillAsync("100");
        await ingSheet.Locator("select:visible").SelectOptionAsync(new SelectOptionValue { Label = "g" });
        await ingSheet.Locator(".sheet__actions button.btn--primary").First.ClickAsync();
        await Assertions.Expect(ingSheet).Not.ToBeVisibleAsync();

        await page.ClickAsync("button[type=submit]:has-text('Create Recipe')");
        await page.WaitForURLAsync(DetailUrlPattern);

        var recipeId = new Uri(page.Url).Segments.Last().TrimEnd('/');
        Assert.True(Guid.TryParse(recipeId, out _), $"Expected recipe GUID in URL, got: {page.Url}");
        return recipeId;
    }

    /// <summary>
    /// Reads the ISO week date currently bound to the Shop control's Alpine <c>x-init</c> attribute —
    /// the exact value <c>shop()</c> will post as the "week" form field. Reading the live attribute
    /// (rather than inferring from the visible week label) is the direct, mechanism-level check that
    /// the OOB rehydration in <c>_ShopButton.cshtml</c> actually ran.
    ///
    /// <paramref name="previousWeek"/>, when supplied, makes this call race-safe against the caller's
    /// own <c>RunAndWaitForResponseAsync</c>: that helper resolves once the HTTP response is
    /// RECEIVED, not once htmx has finished applying the primary swap and the OOB
    /// <c>#plan-shop-btn</c> replacement — so reading the attribute immediately afterward can still
    /// observe the pre-swap node. Passing the week being navigated AWAY FROM makes this poll until
    /// the attribute has actually changed before reading it, instead of asserting on a stale snapshot.
    /// </summary>
    private static async Task<string> ReadShopButtonWeekAsync(IPage page, string? previousWeek = null)
    {
        var shopRoot = page.Locator("#plan-shop-btn");
        await Assertions.Expect(shopRoot).ToHaveCountAsync(1);

        if (previousWeek is not null)
        {
            await page.WaitForFunctionAsync(
                "prev => { const el = document.querySelector('#plan-shop-btn'); return !!el && el.getAttribute('x-init') !== null && !el.getAttribute('x-init').includes(prev); }",
                previousWeek);
        }

        var xInit = await shopRoot.GetAttributeAsync("x-init");
        Assert.NotNull(xInit);
        var match = WeekInitPattern.Match(xInit!);
        Assert.True(match.Success, $"Could not parse week date out of x-init='{xInit}'.");
        return match.Groups[1].Value;
    }

    /// <summary>
    /// Clicks the Shop button and captures the literal "week" form field posted to
    /// <c>/MealPlan?handler=Shop</c> via network-request interception, alongside the response status.
    /// This is the most direct possible proof of what <c>OnPostShopAsync</c> — and therefore
    /// <c>ShopForWeekService.ExecuteAsync</c> — actually received, independent of any DOM/label
    /// reading that could itself be stale.
    /// </summary>
    private static async Task<(int Status, string? PostedWeek)> ClickShopAndCaptureRequestAsync(IPage page, ILocator shopButton)
    {
        IRequest? capturedRequest = null;
        void OnRequest(object? _, IRequest req)
        {
            if (req.Url.Contains("handler=Shop") && req.Method == "POST")
            {
                capturedRequest = req;
            }
        }

        page.Request += OnRequest;
        try
        {
            var response = await page.RunAndWaitForResponseAsync(
                () => shopButton.ClickAsync(),
                resp => resp.Url.Contains("handler=Shop") && resp.Request.Method == "POST");

            Assert.NotNull(capturedRequest);
            var postData = capturedRequest!.PostData;
            Assert.NotNull(postData);
            return (response.Status, ParseFormField(postData!, "week"));
        }
        finally
        {
            page.Request -= OnRequest;
        }
    }

    /// <summary>Minimal application/x-www-form-urlencoded field parser — avoids pulling in a whole
    /// query-string-parsing package for a single field lookup on a POST body already in hand.</summary>
    private static string? ParseFormField(string formBody, string fieldName)
    {
        foreach (var pair in formBody.Split('&'))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0) continue;
            var key = Uri.UnescapeDataString(pair[..idx].Replace('+', ' '));
            if (key != fieldName) continue;
            return Uri.UnescapeDataString(pair[(idx + 1)..].Replace('+', ' '));
        }
        return null;
    }

    private static async Task<string> GetAntiforgeryTokenAsync(IPage page)
    {
        return await page.Locator("input[name=__RequestVerificationToken]").First
                   .GetAttributeAsync("value") ?? "";
    }

    private async Task<Guid?> GetProductIdAsync(string productName)
    {
        await using var conn = new NpgsqlConnection(appHost.DbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id FROM catalog.products WHERE name = @n LIMIT 1", conn);
        cmd.Parameters.AddWithValue("@n", productName);
        return await cmd.ExecuteScalarAsync() as Guid?;
    }

    private async Task<bool> ShoppingItemHasMealPlanSourceAsync(Guid productId)
    {
        await using var conn = new NpgsqlConnection(appHost.DbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"SELECT COUNT(*) FROM shopping.shopping_list_item i
              JOIN shopping.shopping_list_item_contribution c
                ON c.shopping_list_item_id = i.shopping_list_item_id
              WHERE i.product_id = @p AND c.source = 'meal_plan'",
            conn);
        cmd.Parameters.AddWithValue("@p", productId);
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        return count > 0;
    }
}
