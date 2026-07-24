using System.Net;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 render test proving the Details page threads a unit's <c>DisplayStyle.Fraction</c> flag onto the
/// rendered client-side servings-stepper scaler call (plantry-95w5). The bug: the JS twin bridged onto
/// <c>fmt()</c> was decimal-only, so a ¼-cup ingredient read "¼ cup" at 1× but "0.125 cups" — not "⅛
/// cup" — the moment the stepper scaled it. The actual snap arithmetic is pinned exhaustively at the JS
/// layer (<c>ingredient-amount.test.js</c>); this test only proves the wiring — that
/// <c>ICatalogProductReader.ResolveUnitDisplayStylesAsync</c> reaches <c>_IngredientRow</c>'s <c>fmt()</c>
/// call with the right style argument for a Fraction-styled unit — so the client has what it needs to
/// snap once servings changes off scale 1.
/// </summary>
public sealed class RecipeDetailFractionStyleTests(RecipeDetailFractionStyleFactory factory)
    : IClassFixture<RecipeDetailFractionStyleFactory>
{
    private static readonly HtmlParser Parser = new();

    private async Task<string> GetIngredientRowHtmlAsync()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeDetailFixture.HouseholdAId.ToString());
        var response = await client.GetAsync($"/Recipes/{factory.RecipeId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pageHtml = await response.Content.ReadAsStringAsync();

        var doc = Parser.ParseDocument(pageHtml);
        var row = doc.QuerySelectorAll(".rd-ing-list .rd-ing-row")
            .FirstOrDefault(r => r.QuerySelector(".rd-ing-name")?.TextContent.Contains("Rigatoni") == true)
            ?? throw new InvalidOperationException("Ingredient row not found in the rendered Detail page.");
        return row.OuterHtml;
    }

    [Fact]
    public async Task FractionStyled_Unit_Renders_Client_Scaler_Call_With_Fraction_Style_Flag()
    {
        var rowHtml = await GetIngredientRowHtmlAsync();

        // The client-side rescale call must carry the 'fraction' style so the servings stepper can
        // snap the scaled amount to a vulgar fraction (quantity-display.md Q1/Q4) instead of always
        // rendering a bare decimal — the gap plantry-95w5 fixes.
        Assert.Contains("fmt(0.25, 'fraction')", rowHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecimalStyled_Unit_Still_Renders_Client_Scaler_Call_With_Decimal_Style_Flag()
    {
        // Base fixture factory: "g"/"ea" are both Decimal-styled (no unit styles registered), so the
        // existing render paths keep their historical decimal-only scaler call — proving the new flag
        // is opt-in per unit, not a blanket behaviour change for units that were never Fraction-styled.
        using var decimalFactory = new RecipeDetailFragmentFactory();
        var client = decimalFactory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeDetailFixture.HouseholdAId.ToString());
        var response = await client.GetAsync($"/Recipes/{decimalFactory.RecipeId}");
        var pageHtml = await response.Content.ReadAsStringAsync();

        Assert.Contains("fmt(400, 'decimal')", pageHtml, StringComparison.Ordinal);
    }
}
