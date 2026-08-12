using Plantry.Pantry.Application;
using Plantry.Pantry.Domain;
using PantryPage = Plantry.Web.Pages.Pantry.IndexModel;

namespace Plantry.Tests.Web.Grids;

public sealed class PantryProducedFilterTests
{
    private static PantryListItem Row(string name, bool produced, bool stocked = true) => new(
        ProductId: Guid.NewGuid(), Name: name, CategoryName: "Category", LocationDisplay: stocked ? "Pantry" : null,
        IsVariant: false, TotalQuantity: stocked ? 2m : 0m, DisplayUnitCode: "ea", LotCount: stocked ? 1 : 0,
        SoonestExpiry: null, ExpiryTone: ExpiryTone.None, IsRunningLow: false, IsStocked: stocked,
        IsParent: false, IsArchived: false, IsProduced: produced);

    [Fact]
    public void ProducedOnlyFalse_KeepsAllRows()
    {
        var rows = new[] { Row("Purchased", false), Row("Homemade", true) };
        var result = PantryPage.FilterForTests(rows, null, null, null, null, false, false);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ProducedOnlyTrue_KeepsCanonicalProducedRowsRegardlessOfStock()
    {
        var rows = new[] { Row("Purchased", false), Row("Homemade", true, stocked: false) };
        var result = PantryPage.FilterForTests(rows, null, null, null, null, false, true);
        var row = Assert.Single(result);
        Assert.Equal("Homemade", row.Name);
        Assert.False(row.IsStocked);
    }

    [Fact]
    public void ProducedFilter_ComposesWithSearchExpiryCategoryLocationAndVariants()
    {
        var rows = new[]
        {
            Row("Homemade soup", true),
            Row("Homemade salad", true),
            Row("Purchased soup", false),
        };
        var result = PantryPage.FilterForTests(rows, "soup", null, "Category", "Pantry", true, true);
        Assert.Single(result);
        Assert.Equal("Homemade soup", result[0].Name);
    }

    [Fact]
    public void RazorContract_HasExactlyOneProducedCheckboxWithHtmxFlow()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Plantry.Web", "Pages", "Pantry", "Index.cshtml");
        var markup = File.ReadAllText(Path.GetFullPath(path));
        var start = markup.IndexOf("name=\"produced\"", StringComparison.Ordinal);
        Assert.True(start >= 0);
        Assert.Equal(-1, markup.IndexOf("name=\"produced\"", start + 1, StringComparison.Ordinal));
        var input = markup.Substring(markup.LastIndexOf("<input", start), markup.IndexOf("/>", start) + 2 - markup.LastIndexOf("<input", start));
        Assert.Contains("type=\"checkbox\"", input);
        Assert.Contains("value=\"true\"", input);
        Assert.Contains("class=\"pantry-filter\"", input);
        Assert.Contains("hx-trigger=\"change\"", input);
        Assert.Contains("hx-target=\"#pantry-list\"", input);
        Assert.Contains("hx-swap=\"outerHTML\"", input);
        Assert.Contains("hx-include=\".pantry-filter\"", input);
    }

    [Fact]
    public void EverythingMerge_PreservesProducedValueOnSynthesizedRows()
    {
        var catalog = new[] { new ProductListItem(ProductId.New(), "Homemade", "Category", "ea", false, false, false, true) };
        var merged = PantryPage.MergeEverythingScope([], catalog);
        Assert.True(Assert.Single(merged).IsProduced);
    }
}