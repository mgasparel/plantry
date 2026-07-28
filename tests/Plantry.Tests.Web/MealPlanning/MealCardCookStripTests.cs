using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Identity.Infrastructure;
using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;
using Plantry.Tests.Web.MealPlanning;
using Plantry.Tests.Web.Preferences;
using Plantry.Web.MealPlanning;
using Xunit;

namespace Plantry.Tests.Web.MealPlanning;

/// <summary>
/// L4 fragment tests for the plan card Cook strip (plantry-0eut): pending recipe-dish Cook links,
/// done rows, the fully-cooked pill/card treatment, and the today/past-vs-future/note gating. The
/// real <c>IMealPlanCookStatusReader</c> composition adapter is swapped for a fixed fake here (this
/// suite owns the strip's RENDERING contract, not the adapter's derivation logic — that is covered by
/// <c>MealPlanCookStatusReaderAdapterTests</c> at L2 and by the EF read-side integration tests).
/// </summary>
public sealed class MealCardCookStripTests
{
    [Fact(DisplayName = "GET /MealPlan: partially-cooked meal shows a done row and a pending Cook link")]
    public async Task Partially_Cooked_Meal_Shows_Done_Row_And_Cook_Link()
    {
        await using var factory = new MealCardCookStripFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, CookStripFixture.HouseholdId.ToString());

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Breakfast: pending dish (servings=2, multi-dish meal → disambiguated "Cook <name>" label)
        // renders a live Cook deep-link carrying id/servings/plannedDishId.
        Assert.Contains("mc-cook-act", html);
        Assert.Contains("Cook Unknown recipe", html);
        Assert.Contains($"/Recipes/{factory.Repo.PendingRecipeRecipeId:D}/Cook?servings=", html);
        Assert.Contains("servings=2", html);
        Assert.Contains($"plannedDishId={factory.Repo.PendingRecipeDishId:D}", html);
        // plantry-iejb's leftover-prefill seam: eatingTonight = the meal's AttendeesOverride count,
        // carried onto the plan-launched Cook link (Cook.cshtml.cs EatingTonight doc comment).
        Assert.Contains($"eatingTonight={factory.Repo.EatingTonightForBreakfast}", html);

        // Breakfast's other dish (servings=3) is already done — a settled row, not a button.
        Assert.Contains("mc-cook-done", html);
        Assert.Contains("Cooked · 3 srv", html);
    }

    [Fact(DisplayName = "GET /MealPlan: a meal whose every dish is done gets the cooked pill + card treatment")]
    public async Task Fully_Cooked_Meal_Shows_Pill_And_Cooked_Card_Class()
    {
        await using var factory = new MealCardCookStripFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, CookStripFixture.HouseholdId.ToString());

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Lunch: single dish (servings=1), done — the ONLY fully-cooked meal on the page.
        Assert.Contains("Cooked · 1 srv", html);
        Assert.Contains("mc-cooked", html); // the corner pill
        Assert.Contains("meal-card  cooked\"", html); // isNote="" + allDishesDone="cooked" → double space
    }

    [Fact(DisplayName = "GET /MealPlan: a note meal never renders a Cook strip, even when dated today")]
    public async Task Note_Meal_Renders_No_Cook_Strip()
    {
        await using var factory = new MealCardCookStripFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, CookStripFixture.HouseholdId.ToString());

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Free note · no dishes", html); // the Dinner note card rendered at all
        // Exactly two strips on the page — Breakfast (partial) and Lunch (fully cooked). The Dinner
        // note card, despite being dated the same as both, contributes a third only if the note
        // branch leaked strip markup — it must not.
        var stripCount = html.Split("mc-cook-strip").Length - 1;
        Assert.Equal(2, stripCount);
    }

    [Fact(DisplayName = "GET /MealPlan?week=<future>: a future dish-based meal renders no Cook strip")]
    public async Task Future_Meal_Renders_No_Cook_Strip()
    {
        await using var factory = new MealCardCookStripFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, CookStripFixture.HouseholdId.ToString());

        var response = await client.GetAsync($"/MealPlan?week={factory.Repo.FutureWeekMonday:yyyy-MM-dd}");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Sanity: the future week's card rendered at all (its one dish, name-resolved to "Unknown
        // recipe" by the empty week read model — same fallback used on the "this week" response).
        Assert.Contains("Unknown recipe", html);
        Assert.DoesNotContain("mc-cook-strip", html);
        Assert.DoesNotContain("mc-cook-act", html);
        Assert.DoesNotContain("mc-cook-done", html);
    }

    [Fact(DisplayName = "GET /MealPlan: the meal-card root carries the click-anywhere markup contract (plantry-ely3, plantry-bg2v)")]
    public async Task MealCard_Root_Carries_ClickAnywhere_Markup_Contract()
    {
        // L4 fast complement to the E2E journey (MealCardClickAnywhereJourneyTests): pins the
        // rendered markup contract so a future edit that drops the guard or the hidden button turns
        // this test red immediately, without booting a browser. The actual click/keyboard BEHAVIOR is
        // proven at L5 by MealCardClickAnywhereJourneyTests — a markup assertion alone cannot prove
        // a browser interaction works.
        await using var factory = new MealCardCookStripFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, CookStripFixture.HouseholdId.ToString());

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        var cardMatch = System.Text.RegularExpressions.Regex.Match(html, "<div class=\"meal-card[^>]*>");
        Assert.True(cardMatch.Success, "No .meal-card root found in the rendered page.");
        var cardTag = cardMatch.Value;

        // plantry-bg2v: role="button"/tabindex="0"/onkeydown moved OFF the card div — a button
        // role must not wrap other interactive elements (pencil, cook-strip actions, Cook link).
        Assert.DoesNotContain("role=\"button\"", cardTag);
        Assert.DoesNotContain("tabindex=\"0\"", cardTag);
        // The onclick guard bails when the click target is (or is nested inside) an <a>/<button>,
        // so the pencil, Cook link, Eat/Undo buttons, and the new hidden button keep their own
        // behavior (AC2). The literal guard text is written directly in the .cshtml and is NOT
        // HTML-encoded by Razor; only the @openEditorCall substitution downstream of it is (hence
        // the &amp;&amp; / &#x27; below).
        Assert.Contains("onclick=\"if (!event.target.closest('a,button')) { window.__mealPlannerIsland &amp;&amp; window.__mealPlannerIsland.openEditor(&#x27;", cardTag);

        // plantry-bg2v: the new hidden primary-activation button is the sole keyboard/AT entry
        // point (AC4) — native <button> semantics mean no onkeydown handler is needed. It also
        // wears the shared .sr-only utility (plantry-m375, landed earlier in this epic) instead
        // of duplicating a per-component clip-rect rule.
        var hiddenBtnMatch = System.Text.RegularExpressions.Regex.Match(html, "<button class=\"mc-open-details sr-only\"[^>]*>");
        Assert.True(hiddenBtnMatch.Success, "No .mc-open-details.sr-only button found in the rendered page.");
        // plantry-rhxv: the accessible name is contextualised with day + date + slot (the first
        // meal-card in DOM order is the Breakfast card, per the slot-band-then-day iteration order
        // in _WeekGrid.cshtml) so a week grid's 21 cards no longer announce an identical name.
        Assert.Contains($"aria-label=\"Open meal details — {factory.Repo.ThisWeekMonday:dddd} {factory.Repo.ThisWeekMonday:MMM d}, Breakfast\"", hiddenBtnMatch.Value);
        Assert.Contains("onclick=\"window.__mealPlannerIsland &amp;&amp; window.__mealPlannerIsland.openEditor(&#x27;", hiddenBtnMatch.Value);
    }

    [Fact(DisplayName = "GET /MealPlan: card-level accessible names are contextualised and vary per card (plantry-rhxv)")]
    public async Task Card_AccessibleNames_Vary_Per_Card()
    {
        await using var factory = new MealCardCookStripFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, CookStripFixture.HouseholdId.ToString());

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Breakfast, Lunch, and Dinner all fall on the same day (ThisWeekMonday) but different
        // slots — the day+date component alone would collide across these three cards, so the
        // slot qualifier is what proves the names are actually distinguishable, not merely present.
        var dayDate = $"{factory.Repo.ThisWeekMonday:dddd} {factory.Repo.ThisWeekMonday:MMM d}";
        var breakfastOpenLabel = $"aria-label=\"Open meal details — {dayDate}, Breakfast\"";
        var lunchOpenLabel = $"aria-label=\"Open meal details — {dayDate}, Lunch\"";
        var breakfastEditLabel = $"aria-label=\"Edit meal — {dayDate}, Breakfast\"";
        var lunchEditLabel = $"aria-label=\"Edit meal — {dayDate}, Lunch\"";

        Assert.Contains(breakfastOpenLabel, html);
        Assert.Contains(lunchOpenLabel, html);
        Assert.Contains(breakfastEditLabel, html);
        Assert.Contains(lunchEditLabel, html);

        // Every rendered .mc-open-details / .mc-edit label must be unique across the whole grid —
        // this is the assertion that actually proves per-card variance (plantry-rhxv). AngleSharp
        // DOM parsing, not hand-rolled regex, mirrors the established pattern in this project (e.g.
        // MealCardEnrichmentTests.cs) and avoids an unbounded-match false pass.
        var doc = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        var openLabels = doc.QuerySelectorAll("button.mc-open-details")
            .Select(e => e.GetAttribute("aria-label")).ToList();
        var editLabels = doc.QuerySelectorAll("button.mc-edit")
            .Select(e => e.GetAttribute("aria-label")).ToList();
        // Breakfast + Lunch + Dinner note card (Monday) + the two Wednesday Breakfast multi-meal
        // cards (plantry-0m9h) = 5 cards total.
        Assert.Equal(5, openLabels.Count);
        Assert.Equal(5, editLabels.Count);   // .mc-edit renders before the isNote branch, so same five cards
        Assert.Equal(openLabels.Count, openLabels.Distinct().Count());
        Assert.Equal(editLabels.Count, editLabels.Distinct().Count());

        // plantry-0m9h Part 1: the two Wednesday Breakfast cards share identical day+date+slot context
        // (same cell) — only the "(meal N of M)" ordinal suffix can distinguish them. This is the
        // same-cell distinctness assertion the Distinct() check above proves generically; assert the
        // exact expected wording here too so a regression that drops the suffix (making both cards
        // collide back to the plain day+date+slot label) fails loudly and specifically.
        var multiDayDate = $"{factory.Repo.MultiMealDate:dddd} {factory.Repo.MultiMealDate:MMM d}";
        var multiOpenLabel1 = $"aria-label=\"Open meal details — {multiDayDate}, Breakfast (meal 1 of 2)\"";
        var multiOpenLabel2 = $"aria-label=\"Open meal details — {multiDayDate}, Breakfast (meal 2 of 2)\"";
        var multiEditLabel1 = $"aria-label=\"Edit meal — {multiDayDate}, Breakfast (meal 1 of 2)\"";
        var multiEditLabel2 = $"aria-label=\"Edit meal — {multiDayDate}, Breakfast (meal 2 of 2)\"";
        Assert.Contains(multiOpenLabel1, html);
        Assert.Contains(multiOpenLabel2, html);
        Assert.Contains(multiEditLabel1, html);
        Assert.Contains(multiEditLabel2, html);

        // And the single-meal Breakfast/Lunch/Dinner cards must NOT carry any ordinal suffix — the
        // exact byte-for-byte labels asserted above (with no "(meal N of M)" suffix) already prove
        // this via Assert.Contains; a MealCount==1 card that wrongly grew a suffix would fail those
        // Contains assertions since the label text would no longer match verbatim. Belt-and-braces:
        // "(meal 1 of 1)" must never appear — that wording is only valid for MealCount > 1.
        Assert.DoesNotContain("(meal 1 of 1)", html);
    }

    [Fact(DisplayName = "GET /MealPlan: static cell-level labels (add-meal/empty-add/empty-auto) are contextualised and vary per cell (plantry-0m9h)")]
    public async Task StaticCellLabels_Vary_Per_Cell()
    {
        await using var factory = new MealCardCookStripFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, CookStripFixture.HouseholdId.ToString());

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        var doc = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

        // .add-meal renders once per filled cell, alongside its meal card(s) — Breakfast, Lunch,
        // Dinner (Monday) + the multi-meal Wednesday Breakfast cell = 4 filled cells, each with a
        // distinct day+date+slot qualifier.
        var addMealLabels = doc.QuerySelectorAll("button.add-meal")
            .Select(e => e.GetAttribute("aria-label")).ToList();
        Assert.Equal(4, addMealLabels.Count);
        Assert.All(addMealLabels, l => Assert.Matches(@"^Add another meal — .+, .+$", l!));
        Assert.Equal(addMealLabels.Count, addMealLabels.Distinct().Count());
        // Pin the exact owner-approved wording (not just its shape) on a known filled cell —
        // otherwise a change that keeps the regex/Distinct assertions green (e.g. dropping the day
        // name or reordering the date parts) would slip through undetected.
        var mondayDayDate = $"{factory.Repo.ThisWeekMonday:dddd} {factory.Repo.ThisWeekMonday:MMM d}";
        Assert.Contains($"aria-label=\"Add another meal — {mondayDayDate}, Breakfast\"", html);

        // .empty-add / .empty-auto render once per empty cell — every cell in the 3-slot × 7-day
        // grid that isn't one of the 4 filled cells above (this fixture has no ghost/conflict cells:
        // NullPendingProposalStore never stages a proposal and NullTagReader reports every tag
        // fulfillable). Each must carry a distinct day+date+slot qualifier too.
        var emptyAddLabels = doc.QuerySelectorAll("button.empty-add")
            .Select(e => e.GetAttribute("aria-label")).ToList();
        var emptyAutoLabels = doc.QuerySelectorAll("button.empty-auto")
            .Select(e => e.GetAttribute("aria-label")).ToList();
        Assert.Equal(21 - 4, emptyAddLabels.Count);
        Assert.Equal(21 - 4, emptyAutoLabels.Count);
        Assert.All(emptyAddLabels, l => Assert.Matches(@"^Add meal — .+, .+$", l!));
        Assert.All(emptyAutoLabels, l => Assert.Matches(@"^Auto-fill this cell — .+, .+$", l!));
        Assert.Equal(emptyAddLabels.Count, emptyAddLabels.Distinct().Count());
        Assert.Equal(emptyAutoLabels.Count, emptyAutoLabels.Distinct().Count());

        // Tuesday is empty in every slot (only Monday and Wednesday carry meals) — pin the exact
        // owner-approved qualifier wording on a known-empty cell, not just its shape.
        var emptyCellDate = factory.Repo.ThisWeekMonday.AddDays(1);
        var emptyDayDate = $"{emptyCellDate:dddd} {emptyCellDate:MMM d}";
        Assert.Contains($"aria-label=\"Add meal — {emptyDayDate}, Breakfast\"", html);
        Assert.Contains($"aria-label=\"Auto-fill this cell — {emptyDayDate}, Breakfast\"", html);
    }
}

/// <summary>WAF factory wiring a fixed cook-status fake and a two-week meal plan fixture (plantry-0eut).</summary>
public sealed class MealCardCookStripFactory : WebApplicationFactory<Program>
{
    public CookStripMealPlanRepo Repo { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.AddFakeDisplayCurrency("USD");
            services.AddFakeExpiringSoonHorizon();
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.RemoveAll<UserManager<AppUser>>();
            services.AddSingleton<UserManager<AppUser>>(
                new FakeUserManager(new AppUser { Id = "00000000-0000-0000-0000-0000000000cc" }));

            services.RemoveAll<IMealPlanRepository>();
            services.AddSingleton<IMealPlanRepository>(Repo);

            services.RemoveAll<IMealSlotConfigRepository>();
            services.AddScoped<IMealSlotConfigRepository>(_ => new FakeSlotRepo(CookStripFixture.SlotConfig));

            services.RemoveAll<IHouseholdMemberReader>();
            services.AddSingleton<IHouseholdMemberReader>(new FakeMemberReader([]));

            services.RemoveAll<IRecipeReadModel>();
            services.AddSingleton<IRecipeReadModel>(new FakeRecipeReader([]));

            services.RemoveAll<IMealPlanWeekReadModel>();
            services.AddSingleton<IMealPlanWeekReadModel>(new NullWeekReadModel());

            services.RemoveAll<IMealPlanCatalogProductReader>();
            services.AddSingleton<IMealPlanCatalogProductReader>(new FakeCatalogProductReaderW(existsResult: true));

            // The port under test's rendering contract — fixed statuses keyed by the plan's REAL
            // (repo-generated) PlannedDish ids, captured by CookStripMealPlanRepo at construction.
            services.RemoveAll<IMealPlanCookStatusReader>();
            services.AddSingleton<IMealPlanCookStatusReader>(new FixedCookStatusReader(
                new Dictionary<Guid, DishCookStatus>
                {
                    [Repo.DoneRecipeDishIdA] = new DishCookStatus(MealPlanningTestClock.Instant.AddMinutes(-30)),
                    [Repo.DoneRecipeDishIdB] = new DishCookStatus(MealPlanningTestClock.Instant.AddMinutes(-10)),
                }));

            services.RemoveAll<IMealPlanStockReader>();
            services.AddSingleton<IMealPlanStockReader>(new NullStockReader());
            services.RemoveAll<IMealPlanPriceReader>();
            services.AddSingleton<IMealPlanPriceReader>(new NullPriceReader());
            services.RemoveAll<IMealPlanShoppingWriter>();
            services.AddSingleton<IMealPlanShoppingWriter>(new NullShoppingWriter());

            services.RemoveAll<PlanFulfillmentService>();
            services.AddScoped<PlanFulfillmentService>();
            services.RemoveAll<PlanCostingService>();
            services.AddScoped<PlanCostingService>();
            services.RemoveAll<ShopForWeekService>();
            services.AddScoped<ShopForWeekService>();

            services.RemoveAll<AssignMealService>();
            services.AddScoped<AssignMealService>();
            services.RemoveAll<MoveMealService>();
            services.AddScoped<MoveMealService>();

            services.RemoveAll<IMealPlanner>();
            services.AddSingleton<IMealPlanner>(new NullMealPlanner());
            services.RemoveAll<IPendingProposalStore>();
            services.AddSingleton<IPendingProposalStore>(new NullPendingProposalStore());
            services.RemoveAll<GeneratePlanService>();
            services.AddScoped<GeneratePlanService>();
            services.RemoveAll<AcceptProposalService>();
            services.AddScoped<AcceptProposalService>();

            services.RemoveAll<IUserPreferenceRepository>();
            services.AddSingleton<IUserPreferenceRepository>(new NullPrefsRepo());

            services.RemoveAll<ITagReader>();
            services.AddSingleton<ITagReader>(new NullTagReader());

            services.RemoveAll<IMealPlanExpiringStockReader>();
            services.AddSingleton<IMealPlanExpiringStockReader>(new NullExpiringStockReader());
            services.RemoveAll<PlanInsightsService>();
            services.AddScoped<PlanInsightsService>();

            services.RemoveAll<IHouseholdPlanningSettingsRepository>();
            services.AddSingleton<IHouseholdPlanningSettingsRepository>(new NullPlanningSettingsRepo());
            services.RemoveAll<IWeekPlanningOverrideRepository>();
            services.AddSingleton<IWeekPlanningOverrideRepository>(new NullWeekOverrideRepo());
            services.RemoveAll<SetPlanningSettingsService>();
            services.AddScoped<SetPlanningSettingsService>();

            // Pin IClock to the same instant the fixture below derives "today" from (plantry-1w87), so the
            // SUT and the fixture never race two independent reads of the real system clock.
            services.RemoveAll<IClock>();
            services.AddScoped<IClock>(_ => new FixedClock(MealPlanningTestClock.Instant));
        });
    }
}

// ── Cook-strip test doubles ────────────────────────────────────────────────────

/// <summary>Shared stable slot identifiers for the cook-strip test scenario.</summary>
internal static class CookStripFixture
{
    public static readonly Guid HouseholdId = Guid.Parse("55555555-0000-0000-0000-000000000005");

    private static readonly HouseholdId HhId = SharedKernel.HouseholdId.From(HouseholdId);
    public static readonly MealSlotConfig SlotConfig = MealSlotConfig.CreateWithDefaults(HhId, new FixedClock(MealPlanningTestClock.Instant));

    private static readonly List<MealSlot> OrderedSlots = [.. SlotConfig.Slots.OrderBy(s => s.Ordinal)];
    public static readonly MealSlotId BreakfastSlotId = OrderedSlots[0].Id;
    public static readonly MealSlotId LunchSlotId = OrderedSlots[1].Id;
    public static readonly MealSlotId DinnerSlotId = OrderedSlots[2].Id;
}

/// <summary>
/// Meal plan repo backing the cook-strip scenario: TWO weeks (this week + 60 days out), so the
/// gating tests can request each independently via <c>?week=</c>. This week carries a partially-
/// cooked meal (Breakfast), a fully-cooked meal (Lunch), and a note meal (Dinner) — all dated the
/// week's Monday, which is always <c>&lt;= today</c>. The future week carries one pending dish-based
/// meal, dated 60 days out so it is unambiguously future regardless of which day of the week "today"
/// happens to be when the suite runs.
/// </summary>
public sealed class CookStripMealPlanRepo : IMealPlanRepository
{
    public MealPlan ThisWeekPlan { get; }
    public MealPlan FutureWeekPlan { get; }
    public DateOnly ThisWeekMonday { get; }
    public DateOnly FutureWeekMonday { get; }

    /// <summary>The recipe id backing the Breakfast card's still-pending dish — used to assert the Cook link's <c>id=</c>.</summary>
    public Guid PendingRecipeRecipeId { get; } = Guid.CreateVersion7();

    public Guid PendingRecipeDishId { get; private set; }
    public Guid DoneRecipeDishIdA { get; private set; }
    public Guid DoneRecipeDishIdB { get; private set; }
    public Guid FutureRecipeDishId { get; private set; }

    /// <summary>Expected <c>eatingTonight</c> value on the Breakfast Cook link — the meal's AttendeesOverride count.</summary>
    public int EatingTonightForBreakfast { get; private set; }

    /// <summary>
    /// plantry-0m9h: Wednesday of "this week" carries TWO meals in the same Breakfast slot — proves
    /// the per-card ordinal suffix ("(meal N of M)") disambiguates two cards that would otherwise
    /// render byte-identical day+date+slot accessible names.
    /// </summary>
    public DateOnly MultiMealDate { get; }

    public CookStripMealPlanRepo()
    {
        var hhId = SharedKernel.HouseholdId.From(CookStripFixture.HouseholdId);
        var clock = new FixedClock(MealPlanningTestClock.Instant);
        var today = DateOnly.FromDateTime(MealPlanningTestClock.Instant.UtcDateTime);
        ThisWeekMonday = MealPlan.NormalizeToMonday(today);
        FutureWeekMonday = MealPlan.NormalizeToMonday(today.AddDays(60));

        var recipeDoneA = Guid.CreateVersion7();
        var recipeDoneB = Guid.CreateVersion7();
        var recipeFuture = Guid.CreateVersion7();

        // Two attendees for the Breakfast meal — proves the Cook link's eatingTonight param carries
        // the REAL AttendeesOverride count (plantry-iejb's leftover-prefill seam) rather than a
        // coincidental zero default.
        var breakfastAttendees = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        EatingTonightForBreakfast = breakfastAttendees.Count;

        ThisWeekPlan = MealPlan.Start(hhId, ThisWeekMonday, clock);
        // Breakfast: partially cooked — one pending dish, one already-done dish.
        ThisWeekPlan.AssignMeal(ThisWeekMonday, CookStripFixture.BreakfastSlotId,
            [
                new DishSpec(DishKind.Recipe, PendingRecipeRecipeId, 2),
                new DishSpec(DishKind.Recipe, recipeDoneA, 3),
            ],
            breakfastAttendees, "manual", Guid.Empty, clock);
        // Lunch: fully cooked — single done dish.
        ThisWeekPlan.AssignMeal(ThisWeekMonday, CookStripFixture.LunchSlotId,
            [new DishSpec(DishKind.Recipe, recipeDoneB, 1)],
            null, "manual", Guid.Empty, clock);
        // Dinner: note meal — must never render a strip, regardless of date.
        ThisWeekPlan.AssignNote(ThisWeekMonday, CookStripFixture.DinnerSlotId, "Takeout night", null, "manual", Guid.Empty, clock);

        // plantry-0m9h: Wednesday Breakfast — two dish-based meals in the SAME cell (same date+slot),
        // appended via mealId: null so each gets its own ordinal (MP-O8 append). Their cards share
        // byte-identical day+date+slot context; only the "(meal N of M)" ordinal suffix distinguishes
        // them. Dated Monday+2 (strictly after "today" = ThisWeekMonday+1, a fixed Tuesday per
        // MealPlanningTestClock.Instant) so showCookStrip stays false for both — keeps this fixture's
        // cook-strip count (Note_Meal_Renders_No_Cook_Strip) unaffected by the new cell.
        MultiMealDate = ThisWeekMonday.AddDays(2);
        ThisWeekPlan.AssignMeal(MultiMealDate, CookStripFixture.BreakfastSlotId,
            [new DishSpec(DishKind.Recipe, Guid.CreateVersion7(), 1)],
            null, "manual", Guid.Empty, clock);
        ThisWeekPlan.AssignMeal(MultiMealDate, CookStripFixture.BreakfastSlotId,
            [new DishSpec(DishKind.Recipe, Guid.CreateVersion7(), 1)],
            null, "manual", Guid.Empty, clock);

        FutureWeekPlan = MealPlan.Start(hhId, FutureWeekMonday, clock);
        FutureWeekPlan.AssignMeal(FutureWeekMonday, CookStripFixture.BreakfastSlotId,
            [new DishSpec(DishKind.Recipe, recipeFuture, 4)],
            null, "manual", Guid.Empty, clock);

        // Capture the REAL repo-generated PlannedDish ids so the fixed cook-status fake and the
        // test assertions can key off them.
        var breakfast = ThisWeekPlan.PlannedMeals.Single(m => m.MealSlotId == CookStripFixture.BreakfastSlotId && m.Date == ThisWeekMonday);
        PendingRecipeDishId = breakfast.PlannedDishes.Single(d => d.RecipeId == PendingRecipeRecipeId).Id.Value;
        DoneRecipeDishIdA = breakfast.PlannedDishes.Single(d => d.RecipeId == recipeDoneA).Id.Value;

        var lunch = ThisWeekPlan.PlannedMeals.Single(m => m.MealSlotId == CookStripFixture.LunchSlotId);
        DoneRecipeDishIdB = lunch.PlannedDishes.Single(d => d.RecipeId == recipeDoneB).Id.Value;

        var future = FutureWeekPlan.PlannedMeals.Single(m => m.MealSlotId == CookStripFixture.BreakfastSlotId);
        FutureRecipeDishId = future.PlannedDishes.Single(d => d.RecipeId == recipeFuture).Id.Value;
    }

    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
    {
        if (weekStart == ThisWeekMonday) return Task.FromResult<MealPlan?>(ThisWeekPlan);
        if (weekStart == FutureWeekMonday) return Task.FromResult<MealPlan?>(FutureWeekPlan);
        return Task.FromResult<MealPlan?>(null);
    }

    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default) =>
        Task.FromResult(weekStart == FutureWeekMonday ? FutureWeekPlan : ThisWeekPlan);

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Fixed <see cref="IMealPlanCookStatusReader"/> — returns exactly the pre-seeded statuses, filtered to what was asked for.</summary>
internal sealed class FixedCookStatusReader(IReadOnlyDictionary<Guid, DishCookStatus> statuses) : IMealPlanCookStatusReader
{
    public Task<IReadOnlyDictionary<Guid, DishCookStatus>> GetStatusesAsync(
        IReadOnlyCollection<Guid> plannedDishIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, DishCookStatus> result = plannedDishIds
            .Where(statuses.ContainsKey)
            .ToDictionary(id => id, id => statuses[id]);
        return Task.FromResult(result);
    }
}
