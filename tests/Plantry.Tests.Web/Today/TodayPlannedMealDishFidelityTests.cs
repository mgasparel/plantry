using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Today;

/// <summary>
/// L4 fragment tests for the Today planned-meals band's per-dish fidelity (plantry-nlg4):
/// a product dish now carries its real name and the product's own unit ("5 lb") instead of the
/// hardcoded "Product dish" label and a "servings" quantity, and a mixed meal shows every dish's
/// own quantity/unit in the sub-line instead of a single, possibly-mispaired meta quantity.
///
/// Fixture (see <see cref="TodayDishFidelityFactory"/>):
/// <list type="bullet">
///   <item>Breakfast: single product dish "Chicken thighs", 5, unit "lb" (AC1).</item>
///   <item>Lunch: mixed — product "Rice", 3, unresolved unit → "?" (AC4), at ordinal 0; recipe
///     "Pasta Bake", 2 servings, at ordinal 1 (AC3).</item>
///   <item>Dinner: single recipe dish, 1 serving — singular pluralisation (AC2).</item>
/// </list>
/// </summary>
public sealed class TodayPlannedMealDishFidelityTests(TodayDishFidelityFactory factory)
    : IClassFixture<TodayDishFidelityFactory>
{
    private static readonly HtmlParser Parser = new();

    private async Task<IDocument> GetTodayDocAsync()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            TodayDishFidelityFixture.HouseholdId.ToString());
        var response = await client.GetAsync("/Today");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        return Parser.ParseDocument(html);
    }

    /// <summary>Finds the planned-slot card whose slot label starts with <paramref name="label"/>.</summary>
    private static IElement GetSlotCard(IDocument doc, string label)
    {
        var card = doc.QuerySelectorAll(".today-meal-slot--planned")
            .FirstOrDefault(el => el.QuerySelector(".today-slot-label")?.TextContent.TrimStart().StartsWith(label, StringComparison.Ordinal) == true);
        Assert.NotNull(card);
        return card!;
    }

    // ── AC1: single product dish shows real name + product unit ────────────────

    [Fact(DisplayName = "AC1 — single product dish shows its real name, not 'Product dish'")]
    public async Task ProductDish_ShowsRealName()
    {
        var doc = await GetTodayDocAsync();
        var breakfast = GetSlotCard(doc, "Breakfast");

        Assert.Contains("Chicken thighs", breakfast.TextContent);
        Assert.DoesNotContain("Product dish", doc.Body!.TextContent, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "AC1 — single product dish's meta quantity shows '5 lb', not '5 servings'")]
    public async Task ProductDish_MetaShowsProductUnit()
    {
        var doc = await GetTodayDocAsync();
        var breakfast = GetSlotCard(doc, "Breakfast");
        var meta = breakfast.QuerySelector(".today-meal-meta");
        Assert.NotNull(meta);

        Assert.Contains("5 lb", meta!.TextContent);
        Assert.DoesNotContain("serving", meta.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    // ── AC2: recipe dish pluralisation ──────────────────────────────────────────

    [Fact(DisplayName = "AC2 — single recipe dish of 1 renders '1 serving' (singular)")]
    public async Task RecipeDish_SingularServing()
    {
        var doc = await GetTodayDocAsync();
        var dinner = GetSlotCard(doc, "Dinner");
        var meta = dinner.QuerySelector(".today-meal-meta");
        Assert.NotNull(meta);

        Assert.Contains("1 serving", meta!.TextContent);
        Assert.DoesNotContain("1 servings", meta.TextContent, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "AC2 — a recipe dish of 2 renders '2 servings' (plural) in the sub-line")]
    public async Task RecipeDish_PluralServings()
    {
        var doc = await GetTodayDocAsync();
        var lunch = GetSlotCard(doc, "Lunch");
        var dishes = lunch.QuerySelector(".today-meal-dishes");
        Assert.NotNull(dishes);

        Assert.Contains("2 servings", dishes!.TextContent);
    }

    // ── AC3: mixed meal shows both dishes' own quantity/unit, meta suppressed ──

    [Fact(DisplayName = "AC3 — mixed meal's sub-line shows both dishes, each with its own quantity/unit")]
    public async Task MixedMeal_SubLineShowsBothDishes()
    {
        var doc = await GetTodayDocAsync();
        var lunch = GetSlotCard(doc, "Lunch");
        var dishes = lunch.QuerySelector(".today-meal-dishes");
        Assert.NotNull(dishes);

        Assert.Contains("Rice", dishes!.TextContent);
        Assert.Contains("Pasta Bake", dishes.TextContent);
        Assert.Contains("+", dishes.TextContent, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "AC3 — mixed meal shows no single meta quantity (ambiguous for 2+ dishes)")]
    public async Task MixedMeal_NoMetaQuantity()
    {
        var doc = await GetTodayDocAsync();
        var lunch = GetSlotCard(doc, "Lunch");
        var meta = lunch.QuerySelector(".today-meal-meta");
        Assert.NotNull(meta);

        // Cook-time meta may still render; the servings/quantity meta item specifically must not.
        Assert.DoesNotContain("serving", meta!.TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" lb", meta.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(" ?", meta.TextContent, StringComparison.Ordinal);
    }

    // ── AC4: unresolved unit falls back to "?" ──────────────────────────────────

    [Fact(DisplayName = "AC4 — a product dish whose unit cannot be resolved renders '?', not 'servings'")]
    public async Task ProductDish_UnresolvedUnit_RendersQuestionMark()
    {
        var doc = await GetTodayDocAsync();
        var lunch = GetSlotCard(doc, "Lunch");
        var dishes = lunch.QuerySelector(".today-meal-dishes");
        Assert.NotNull(dishes);

        Assert.Contains("Rice (3 ?)", dishes!.TextContent);
    }
}

// ── AC5: note-based meals render exactly as before ──────────────────────────────

/// <summary>
/// L4 regression test for AC5 (plantry-nlg4): a note-based meal must render identically to before
/// the per-dish refactor — the note text as-is, no dish sub-line, no servings/quantity meta.
/// </summary>
public sealed class TodayNoteMealDishFidelityTests(TodayNoteMealDishFidelityFactory factory)
    : IClassFixture<TodayNoteMealDishFidelityFactory>
{
    private static readonly HtmlParser Parser = new();

    [Fact(DisplayName = "AC5 — note-based meal renders the note text, no dish sub-line, no quantity meta")]
    public async Task NoteMeal_RendersUnaffected()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            TodayNoteMealDishFidelityFixture.HouseholdId.ToString());
        var response = await client.GetAsync("/Today");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var doc = Parser.ParseDocument(html);

        var noteEl = doc.QuerySelector(".today-meal-name--note");
        Assert.NotNull(noteEl);
        Assert.Equal("Leftover soup", noteEl!.TextContent.Trim());

        var card = noteEl.Closest(".today-meal-slot--planned");
        Assert.NotNull(card);
        Assert.Null(card!.QuerySelector(".today-meal-dishes"));

        var meta = card.QuerySelector(".today-meal-meta");
        Assert.NotNull(meta);
        Assert.DoesNotContain("serving", meta!.TextContent, StringComparison.OrdinalIgnoreCase);
    }
}
