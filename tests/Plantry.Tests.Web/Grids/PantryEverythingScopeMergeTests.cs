using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.Inventory.Application;
using PantryPage = Plantry.Web.Pages.Pantry.IndexModel;

namespace Plantry.Tests.Web.Grids;

/// <summary>
/// Safety-net unit tests for the Pantry "Everything" scope merge (plantry-sjfn):
/// <c>IndexModel.MergeEverythingScope</c> folds every active catalog product absent from the
/// in-stock list in as a synthesized zero-lot row. No page-handler test harness exists for this
/// page (mirrors <c>GridSortTests</c>/<c>PantryExpiryCellTests</c>), so the merge is pinned
/// directly against the extracted internal static method.
/// </summary>
public sealed class PantryEverythingScopeMergeTests
{
    private static PantryListItem Stocked(string name, Guid productId) =>
        new(
            ProductId: productId,
            Name: name,
            CategoryName: "Existing",
            LocationDisplay: "Pantry",
            IsVariant: false,
            TotalQuantity: 2m,
            DisplayUnitCode: "ea",
            LotCount: 1,
            SoonestExpiry: null,
            ExpiryTone: ExpiryTone.None);

    private static ProductListItem Catalog(
        string name, Guid? id = null, string? category = null, bool isVariant = false, bool isParent = false,
        bool isArchived = false) =>
        new(
            Id: id is { } i ? ProductId.From(i) : ProductId.New(),
            Name: name,
            CategoryName: category,
            DefaultUnitCode: "ea",
            IsArchived: isArchived,
            IsVariant: isVariant,
            IsParent: isParent);

    [Fact(DisplayName = "A catalog product already in stock is not duplicated")]
    public void AlreadyStocked_IsNotDuplicated()
    {
        var productId = Guid.NewGuid();
        var inStock = new[] { Stocked("Whole milk", productId) };
        var catalog = new[] { Catalog("Whole milk", productId) };

        var merged = PantryPage.MergeEverythingScope(inStock, catalog);

        Assert.Single(merged);
        Assert.True(merged[0].IsStocked);
        Assert.Equal(2m, merged[0].TotalQuantity);
    }

    [Fact(DisplayName = "A never-stocked active catalog product folds in as a quiet zero-lot row")]
    public void NeverStocked_FoldsInAsUnstockedRow()
    {
        var inStock = Array.Empty<PantryListItem>();
        var catalog = new[] { Catalog("Baking soda", category: "Baking") };

        var merged = PantryPage.MergeEverythingScope(inStock, catalog);

        var row = Assert.Single(merged);
        Assert.False(row.IsStocked);
        Assert.Equal(0, row.LotCount);
        Assert.Equal(0m, row.TotalQuantity);
        Assert.Null(row.LocationDisplay);
        Assert.Null(row.SoonestExpiry);
        Assert.Equal(ExpiryTone.None, row.ExpiryTone);
        Assert.Equal("Baking soda", row.Name);
        Assert.Equal("Baking", row.CategoryName);
    }

    [Fact(DisplayName = "A synthesized parent row carries IsParent for the Kind badge")]
    public void ParentProduct_CarriesIsParent()
    {
        var inStock = Array.Empty<PantryListItem>();
        var catalog = new[] { Catalog("Maple syrup", isParent: true) };

        var merged = PantryPage.MergeEverythingScope(inStock, catalog);

        Assert.True(Assert.Single(merged).IsParent);
    }

    [Fact(DisplayName = "plantry-lxm2: an archived, never-stocked catalog product folds in with IsArchived set")]
    public void ArchivedAndUnstocked_CarriesIsArchived()
    {
        var inStock = Array.Empty<PantryListItem>();
        var catalog = new[] { Catalog("Instant espresso", category: "Beverages", isArchived: true) };

        var merged = PantryPage.MergeEverythingScope(inStock, catalog);

        var row = Assert.Single(merged);
        Assert.True(row.IsArchived);
        Assert.False(row.IsStocked);
    }

    [Fact(DisplayName = "In-stock rows and unstocked catalog rows both appear, stocked rows unchanged")]
    public void MixedSet_KeepsStockedRowsIntactAndAppendsUnstocked()
    {
        var stockedId = Guid.NewGuid();
        var inStock = new[] { Stocked("Frozen peas", stockedId) };
        var catalog = new[]
        {
            Catalog("Frozen peas", stockedId, isVariant: true),
            Catalog("Instant espresso"),
        };

        var merged = PantryPage.MergeEverythingScope(inStock, catalog);

        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, i => i.Name == "Frozen peas" && i.IsStocked && i.TotalQuantity == 2m);
        Assert.Contains(merged, i => i.Name == "Instant espresso" && !i.IsStocked);
    }
}
