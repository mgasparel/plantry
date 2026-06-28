using System.Net;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Today;

/// <summary>
/// L4 fragment tests for the Today page planned-meals band (SPEC Page 0 §0c, plantry-zp7).
/// Each test fetches the real Today page through the WAF (full pipeline, in-memory fakes)
/// and asserts on the rendered HTML fragment.
///
/// Fixture scenario (see <see cref="TodayPlannedMealsBandFactory"/>):
/// <list type="bullet">
///   <item>3 active slots (Breakfast / Lunch / Dinner).</item>
///   <item>Breakfast: planned — recipe "Pasta Carbonara", HasPhoto=true, CookTimeMinutes=20,
///     FulfillmentPercent=100, HasExpiringIngredients=false → ready hint shown, Cook link rendered.</item>
///   <item>Lunch + Dinner: empty → "Nothing planned yet" + "Plan a meal" affordance.</item>
///   <item>AllMealsPlanned=false (2/3 planned) → day-summary card NOT rendered.</item>
/// </list>
///
/// No-slots scenario (see <see cref="TodayPlannedMealsBandNoSlotsFactory"/>):
/// <list type="bullet">
///   <item>No slot config → "No meal slots set up" empty state rendered.</item>
/// </list>
/// </summary>
public sealed class PlannedMealsBandFragmentTests(TodayPlannedMealsBandFactory factory)
    : IClassFixture<TodayPlannedMealsBandFactory>
{
    private static readonly HtmlParser Parser = new();

    private async Task<string> GetTodayHtmlAsync()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            TodayPlannedBandFixture.HouseholdId.ToString());
        var response = await client.GetAsync("/Today");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    // ── Section header ────────────────────────────────────────────────────────

    [Fact(DisplayName = "PlannedBand — section header reads 'Today's meals'")]
    public async Task PlannedBand_SectionHeader_IsTodays()
    {
        var html = await GetTodayHtmlAsync();
        var doc = Parser.ParseDocument(html);
        var h2 = doc.QuerySelector("#today-meals-band .today-section-head h2");
        Assert.NotNull(h2);
        Assert.Equal("Today's meals", h2.TextContent.Trim());
    }

    [Fact(DisplayName = "PlannedBand — section header has 'Open weekly plan' link to /MealPlan")]
    public async Task PlannedBand_SectionHeader_HasWeeklyPlanLink()
    {
        var html = await GetTodayHtmlAsync();
        var doc = Parser.ParseDocument(html);
        var link = doc.QuerySelector("#today-meals-band .today-section-head a[href='/MealPlan']");
        Assert.NotNull(link);
        Assert.Contains("weekly plan", link.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "PlannedBand — section header shows X/Y planned meta")]
    public async Task PlannedBand_SectionHeader_ShowsPlannedMeta()
    {
        var html = await GetTodayHtmlAsync();
        var doc = Parser.ParseDocument(html);
        var meta = doc.QuerySelector("#today-meals-band .today-section-head__meta");
        Assert.NotNull(meta);
        // Should say "1/3 planned" (1 meal planned out of 3 slots)
        Assert.Contains("/", meta.TextContent, StringComparison.Ordinal);
        Assert.Contains("planned", meta.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    // ── Planned slot (Breakfast) ─────────────────────────────────────────────

    [Fact(DisplayName = "PlannedBand — planned slot renders with 'today-meal-slot--planned' class")]
    public async Task PlannedBand_PlannedSlot_HasPlannedClass()
    {
        var html = await GetTodayHtmlAsync();
        var doc = Parser.ParseDocument(html);
        var plannedSlots = doc.QuerySelectorAll(".today-meal-slot--planned");
        Assert.NotEmpty(plannedSlots);
    }

    [Fact(DisplayName = "PlannedBand — planned slot shows recipe name 'Pasta Carbonara'")]
    public async Task PlannedBand_PlannedSlot_ShowsRecipeName()
    {
        var html = await GetTodayHtmlAsync();
        Assert.Contains("Pasta Carbonara", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "PlannedBand — planned slot shows recipe photo img (HasPhoto=true)")]
    public async Task PlannedBand_PlannedSlot_ShowsRecipePhoto()
    {
        var html = await GetTodayHtmlAsync();
        var doc = Parser.ParseDocument(html);
        // HasPhoto=true → <img> with handler=Photo in src
        var imgs = doc.QuerySelectorAll(".today-meal-photo .today-meal-photo__img");
        Assert.NotEmpty(imgs);
        var src = imgs[0].GetAttribute("src") ?? "";
        Assert.Contains("handler=Photo", src, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "PlannedBand — planned slot shows Cook button linking to /Recipes/{id}/Cook")]
    public async Task PlannedBand_PlannedSlot_HasCookButton()
    {
        var html = await GetTodayHtmlAsync();
        var doc = Parser.ParseDocument(html);
        var cookLinks = doc.QuerySelectorAll(".today-meal-slot__cook");
        Assert.NotEmpty(cookLinks);
        var href = cookLinks[0].GetAttribute("href") ?? "";
        Assert.Matches(@"/Recipes/[0-9a-f-]+/Cook", href);
    }

    [Fact(DisplayName = "PlannedBand — planned slot shows ready hint when FulfillmentPercent=100")]
    public async Task PlannedBand_PlannedSlot_ShowsReadyHint()
    {
        var html = await GetTodayHtmlAsync();
        var doc = Parser.ParseDocument(html);
        var readyHints = doc.QuerySelectorAll(".today-meal-hint--ready");
        Assert.NotEmpty(readyHints);
        Assert.Contains(readyHints, h => h.TextContent.Contains("ready to cook", StringComparison.OrdinalIgnoreCase));
    }

    // ── Empty slots (Lunch + Dinner) ─────────────────────────────────────────

    [Fact(DisplayName = "PlannedBand — empty slots render with 'today-meal-slot--empty' class")]
    public async Task PlannedBand_EmptySlots_HaveEmptyClass()
    {
        var html = await GetTodayHtmlAsync();
        var doc = Parser.ParseDocument(html);
        var emptySlots = doc.QuerySelectorAll(".today-meal-slot--empty");
        // 2 empty slots (Lunch + Dinner)
        Assert.Equal(2, emptySlots.Length);
    }

    [Fact(DisplayName = "PlannedBand — empty slot shows 'Nothing planned yet'")]
    public async Task PlannedBand_EmptySlot_ShowsNothingPlannedYet()
    {
        var html = await GetTodayHtmlAsync();
        Assert.Contains("Nothing planned yet", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "PlannedBand — empty slot has 'Plan a meal' link to /MealPlan")]
    public async Task PlannedBand_EmptySlot_HasPlanLink()
    {
        var html = await GetTodayHtmlAsync();
        var doc = Parser.ParseDocument(html);
        // Empty slots show a "Plan a meal" button linking to /MealPlan
        var planLinks = doc.QuerySelectorAll(".today-meal-slot--empty a[href='/MealPlan']");
        Assert.NotEmpty(planLinks);
    }

    // ── AllMealsPlanned / day-summary ────────────────────────────────────────

    [Fact(DisplayName = "PlannedBand — day-summary card is NOT shown when not all slots are planned")]
    public async Task PlannedBand_DaySummary_NotShownWhenNotAllPlanned()
    {
        var html = await GetTodayHtmlAsync();
        var doc = Parser.ParseDocument(html);
        var summary = doc.QuerySelector(".today-day-summary");
        // 1/3 planned → summary should NOT appear
        Assert.Null(summary);
    }

    // ── No shopping hint (all-ready scenario) ────────────────────────────────

    [Fact(DisplayName = "PlannedBand — no shop hint when FulfillmentPercent=100")]
    public async Task PlannedBand_NoShopHint_WhenFullyStocked()
    {
        var html = await GetTodayHtmlAsync();
        var doc = Parser.ParseDocument(html);
        var shopHints = doc.QuerySelectorAll(".today-meal-hint--shop");
        // FulfillmentPercent=100 → only ready hints, no shop hints
        Assert.Empty(shopHints);
    }
}

/// <summary>
/// L4 fragment tests for the no-slots empty state of the planned-meals band (plantry-zp7).
/// When no slot config exists for the household, the band shows "No meal slots set up".
/// </summary>
public sealed class PlannedMealsBandNoSlotsTests(TodayPlannedMealsBandNoSlotsFactory factory)
    : IClassFixture<TodayPlannedMealsBandNoSlotsFactory>
{
    private static readonly HtmlParser Parser = new();

    private async Task<string> GetTodayHtmlAsync()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            TodayPlannedBandFixture.HouseholdId.ToString());
        var response = await client.GetAsync("/Today");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact(DisplayName = "PlannedBand (no slots) — shows 'No meal slots set up' empty state")]
    public async Task PlannedBand_NoSlots_ShowsEmptyState()
    {
        var html = await GetTodayHtmlAsync();
        Assert.Contains("No meal slots set up", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "PlannedBand (no slots) — shows link to /Settings/MealSlots")]
    public async Task PlannedBand_NoSlots_HasSetupLink()
    {
        var html = await GetTodayHtmlAsync();
        var doc = Parser.ParseDocument(html);
        var setupLink = doc.QuerySelector("a[href='/Settings/MealSlots']");
        Assert.NotNull(setupLink);
    }

    [Fact(DisplayName = "PlannedBand (no slots) — no slot cards rendered")]
    public async Task PlannedBand_NoSlots_NoSlotCards()
    {
        var html = await GetTodayHtmlAsync();
        var doc = Parser.ParseDocument(html);
        var slots = doc.QuerySelectorAll(".today-meal-slot");
        Assert.Empty(slots);
    }
}
