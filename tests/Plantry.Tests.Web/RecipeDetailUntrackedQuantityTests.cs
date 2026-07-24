using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 render test proving an untracked ingredient with a real authored quantity still displays that
/// amount on the Detail page, alongside the "untracked" sub-label (plantry-cbww).
///
/// The bug: <c>Details.cshtml.cs</c>'s <c>BuildItem</c> nulled Quantity/UnitCode purely because
/// <c>Product.TrackStock</c> was false, discarding a real authored quantity (e.g. "2 ea" of Salt) — and
/// <c>_IngredientRow.cshtml</c> compounded it with a second <c>!item.IsUntracked</c> guard. Untracked-ness
/// is orthogonal to whether a quantity was supplied (R5): "to taste" (null qty/unit) is one valid case
/// of an untracked ingredient, not the only one.
/// </summary>
public sealed class RecipeDetailUntrackedQuantityTests(RecipeDetailUntrackedQuantityFactory factory)
    : IClassFixture<RecipeDetailUntrackedQuantityFactory>
{
    private static readonly HtmlParser Parser = new();

    private async Task<IElement> GetSaltRowAsync()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeDetailFixture.HouseholdAId.ToString());
        var response = await client.GetAsync($"/Recipes/{factory.RecipeId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pageHtml = await response.Content.ReadAsStringAsync();

        var doc = Parser.ParseDocument(pageHtml);
        return doc.QuerySelectorAll(".rd-ing-list .rd-ing-row")
                   .FirstOrDefault(r => r.QuerySelector(".rd-ing-name")?.TextContent.Contains("Salt") == true)
               ?? throw new InvalidOperationException("Salt ingredient row not found in the rendered Detail page.");
    }

    [Fact]
    public async Task Untracked_ingredient_with_real_quantity_renders_the_amount()
    {
        var row = await GetSaltRowAsync();

        var amount = row.QuerySelector(".rd-ing-amt");
        Assert.NotNull(amount);
        Assert.Contains("2", amount!.TextContent, StringComparison.Ordinal);
        Assert.Contains("ea", amount.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Untracked_ingredient_with_real_quantity_still_shows_untracked_sub_label()
    {
        var row = await GetSaltRowAsync();

        var sub = row.QuerySelector(".rd-ing-sub");
        Assert.NotNull(sub);
        Assert.Contains("untracked", sub!.TextContent, StringComparison.OrdinalIgnoreCase);
    }
}
