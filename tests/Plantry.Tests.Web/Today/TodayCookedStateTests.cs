using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Today;

/// <summary>
/// L4 fragment tests for the cooked-meal CTA gate on the Today planned-meals band (plantry-ohmb):
/// a meal whose every dish is cooked/eaten no longer shows the Cook CTA, and its fulfillment hint
/// is replaced with a cooked-state hint. See <see cref="TodayCookedStateFactory"/> for the fixture
/// scenario (Breakfast = fully cooked recipe, Lunch = partially cooked mixed meal, Dinner = eaten
/// product dish).
/// </summary>
public sealed class TodayCookedStateTests(TodayCookedStateFactory factory)
    : IClassFixture<TodayCookedStateFactory>
{
    private static readonly HtmlParser Parser = new();

    private async Task<string> GetTodayHtmlAsync()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, TodayCookedStateFixture.HouseholdId.ToString());
        var response = await client.GetAsync("/Today");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact(DisplayName = "Cooked meal (AC1) — no Cook link renders, and a done indicator is shown")]
    public async Task CookedMeal_NoCookLink_DoneIndicatorShown()
    {
        var html = await GetTodayHtmlAsync();
        var doc = Parser.ParseDocument(html);

        // AC1: zero /Recipes/{id}/Cook links anywhere on the page — only Breakfast/Lunch reference
        // this recipe and Breakfast is fully cooked; Lunch stays partial (AC2) so its Cook link
        // remains — the recipe id is shared, so this asserts on the done-indicator element instead.
        var doneIndicators = doc.QuerySelectorAll(".today-meal-badge--done");
        Assert.NotEmpty(doneIndicators);
        Assert.Contains(doneIndicators, d => d.TextContent.Contains("Cooked", System.StringComparison.OrdinalIgnoreCase));

        // AC1 (tightened): the fixture plans exactly 3 slots (Breakfast/Lunch/Dinner) with Breakfast
        // and Dinner fully cooked and only Lunch partial — so exactly ONE Cook anchor may survive
        // (Lunch's). This is what actually proves the done indicator REPLACES the Cook CTA rather
        // than rendering alongside it.
        Assert.Single(doc.QuerySelectorAll("a.today-meal-slot__cook"));
    }

    [Fact(DisplayName = "Cooked meal (AC4) — cooked-state hint replaces the ready/shop fulfillment hint")]
    public async Task CookedMeal_ShowsCookedHint_NotFulfillmentHint()
    {
        var html = await GetTodayHtmlAsync();
        var doc = Parser.ParseDocument(html);

        // AC4 (tightened): scope the assertion to each fully-cooked slot's OWN hint element — a
        // page-level string match can't distinguish "Breakfast/Dinner show the cooked hint" from
        // "Lunch's ready/shop hint happens to be on the same page". Breakfast and Dinner are the
        // fixture's two fully-cooked slots (AC1/AC7); both must show ONLY the cooked hint, in the
        // ready tone, and never the fulfillment ready/shop wording it replaces.
        foreach (var label in new[] { "Breakfast", "Dinner" })
        {
            var hint = SlotByLabel(doc, label).QuerySelector(".today-meal-hint")!;
            Assert.Contains("Already cooked today", hint.TextContent, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ready to cook", hint.TextContent, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("pick up first", hint.TextContent, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("today-meal-hint--ready", hint.ClassName ?? "");
            Assert.DoesNotContain("today-meal-hint--shop", hint.ClassName ?? "");
        }

        // Lunch stays partially cooked (AC2) — its hint must NOT show the cooked wording.
        var lunchHint = SlotByLabel(doc, "Lunch").QuerySelector(".today-meal-hint")!;
        Assert.DoesNotContain("Already cooked today", lunchHint.TextContent, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Resolves a single planned slot card by its slot label (e.g. "Breakfast").</summary>
    private static AngleSharp.Dom.IElement SlotByLabel(AngleSharp.Dom.IDocument d, string label) =>
        d.QuerySelectorAll(".today-meal-slot--planned")
            .Single(s => (s.QuerySelector(".today-slot-label")?.TextContent ?? "")
                .Contains(label, System.StringComparison.OrdinalIgnoreCase));

    [Fact(DisplayName = "Partially-cooked multi-dish meal (AC2) — Cook CTA still renders")]
    public async Task PartiallyCookedMeal_StillShowsCookLink()
    {
        var html = await GetTodayHtmlAsync();
        var doc = Parser.ParseDocument(html);

        // Lunch (recipe cooked, product dish not) must still show a live Cook link — AC2's "partial
        // cook keeps the CTA" guarantee. Exactly one Cook link survives (Lunch's) — Breakfast and
        // Dinner are fully cooked and must NOT emit one.
        var cookLinks = doc.QuerySelectorAll("a.today-meal-slot__cook");
        Assert.Single(cookLinks);
    }

    [Fact(DisplayName = "Eaten product-only meal (AC7) — counts as cooked, no recipe-only special-casing")]
    public async Task EatenProductOnlyMeal_CountsAsCooked()
    {
        var html = await GetTodayHtmlAsync();
        var doc = Parser.ParseDocument(html);

        // Dinner (single product dish, net-consumed) must ALSO render a done indicator — proving the
        // port's presence semantics gate the CTA for a product dish exactly like a recipe dish.
        var doneIndicators = doc.QuerySelectorAll(".today-meal-badge--done");
        Assert.Equal(2, doneIndicators.Length); // Breakfast (recipe) + Dinner (product) — Lunch stays partial
    }

}

/// <summary>
/// AC6 regression coverage, isolated to its own factory instance (never shared via
/// <see cref="IClassFixture{TFixture}"/>, unlike <see cref="TodayCookedStateTests"/> above) —
/// <see cref="TodayCookedStateFactory.CookStatusReader"/>'s call counter must observe exactly one
/// page load, and a shared fixture instance would accumulate calls across every other test in the
/// class, making "exactly one" unassertable.
/// </summary>
public sealed class TodayCookedStateBatchingTests
{
    [Fact(DisplayName = "AC6 — exactly one GetStatusesAsync call per page load regardless of slot/dish count")]
    public async Task ExactlyOneCookStatusBatchCallPerPageLoad()
    {
        await using var factory = new TodayCookedStateFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, TodayCookedStateFixture.HouseholdId.ToString());

        var response = await client.GetAsync("/Today");
        response.EnsureSuccessStatusCode();

        Assert.Equal(1, factory.CookStatusReader.GetStatusesAsyncCallCount);
    }
}
