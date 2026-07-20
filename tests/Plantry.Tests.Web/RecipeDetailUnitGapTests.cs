using System.Net;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 render test for the Detail page's unit-gap ingredient state (plantry-z2sr). The dogfood repro:
/// stock is held as a weight (grams) while the recipe line is a count ("ea") with no conversion path,
/// so cookability reads the line as Missing. The row must NOT flatly say "Not in your pantry" — it must
/// distinguish the honest "can't compare units" state with an info-tone status and an explanatory popover
/// linking to the product's Add-conversion page (mirroring the Cook page's IsUnitGap treatment, plantry-qll2.5).
/// </summary>
public sealed class RecipeDetailUnitGapTests(RecipeDetailUnitGapFactory factory)
    : IClassFixture<RecipeDetailUnitGapFactory>
{
    private static readonly HtmlParser Parser = new();

    private async Task<string> GetGarlicRowHtmlAsync()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeDetailFixture.HouseholdAId.ToString());
        var response = await client.GetAsync($"/Recipes/{factory.RecipeId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pageHtml = await response.Content.ReadAsStringAsync();

        var doc = Parser.ParseDocument(pageHtml);
        // Locate the Garlic ingredient row by its resolved product name (row order is not relied upon).
        var garlicRow = doc.QuerySelectorAll(".rd-ing-list .rd-ing-row")
            .FirstOrDefault(row => row.QuerySelector(".rd-ing-name")?.TextContent.Contains("Garlic Cloves") == true)
            ?? throw new InvalidOperationException("Garlic ingredient row not found in the rendered Detail page.");
        return garlicRow.OuterHtml;
    }

    [Fact]
    public async Task Unit_Gap_Row_Shows_Cant_Compare_Units_Not_Not_In_Pantry()
    {
        var rowHtml = await GetGarlicRowHtmlAsync();

        // The honest "can't compare" copy replaces the danger "Not in your pantry" label.
        Assert.Contains("Can't compare units", rowHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Not in your pantry", rowHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unit_Gap_Row_Uses_Info_Tone_Status()
    {
        var rowHtml = await GetGarlicRowHtmlAsync();

        // Info-tone status + icon rather than the danger "miss" treatment.
        Assert.Contains("rd-ing-status--unitgap", rowHtml, StringComparison.Ordinal);
        Assert.Contains("rd-ing-sub--unitgap", rowHtml, StringComparison.Ordinal);
        Assert.Contains("#i-info", rowHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("rd-ing-status--miss", rowHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unit_Gap_Row_Surfaces_Popover_Linking_To_Add_Conversion()
    {
        var rowHtml = await GetGarlicRowHtmlAsync();

        // The <popover> primitive renders, and it links out to the product's Add-conversion page.
        Assert.Contains("popover__content", rowHtml, StringComparison.Ordinal);
        Assert.Contains($"/Catalog/Products/{RecipeDetailFixture.GarlicId}", rowHtml, StringComparison.Ordinal);
        Assert.Contains("Add a conversion", rowHtml, StringComparison.Ordinal);
    }
}
