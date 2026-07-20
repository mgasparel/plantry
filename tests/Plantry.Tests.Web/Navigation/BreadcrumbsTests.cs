using Plantry.Web.Navigation;

namespace Plantry.Tests.Web.Navigation;

/// <summary>
/// L1 regression tests for <see cref="Breadcrumbs"/> — the topbar breadcrumb label,
/// parent-chain, and icon logic extracted from <c>_Layout.cshtml</c>. Locks the
/// PascalCase-split humanisation, dict passthrough, uniform leaf-drop, action-page
/// folder dedup, and the parents-first-then-segments icon fallback so they can no
/// longer only be exercised by rendering the layout.
/// </summary>
public sealed class BreadcrumbsTests
{
    [Fact]
    public void Label_PascalCaseSegment_SplitsIntoWords()
    {
        // "TakeStock" is not in the dict, so it humanises via the PascalCase split.
        Assert.Equal("Take Stock", Breadcrumbs.Label("TakeStock"));
    }

    [Fact]
    public void Label_MultiWordPascalCaseSegment_SplitsEachBoundary()
    {
        Assert.Equal("Stores And Deals", Breadcrumbs.Label("StoresAndDeals"));
    }

    [Fact]
    public void Label_DictSegment_UsesMappedValue()
    {
        // The dict entry wins for "MealPlan" -> "Meal Plan" (rather than any split coincidence).
        Assert.Equal("Meal Plan", Breadcrumbs.Label("MealPlan"));
    }

    [Fact]
    public void Label_EmptySegment_ReturnedAsIs()
    {
        Assert.Equal("", Breadcrumbs.Label(""));
    }

    [Fact]
    public void BuildParents_NamedLeaf_DropsLeafKeepingParent()
    {
        // Settings/Tags: the trailing named-leaf "Tags" is the bold title, so only [Settings] remains.
        Assert.Equal(new[] { "Settings" }, Breadcrumbs.BuildParents("/Settings/Tags", "Tags"));
    }

    [Fact]
    public void BuildParents_FolderIndexPage_DedupsContainingFolder()
    {
        // Catalog/Products/Index: "Index" is action leaf-dropped, and "Products" == title so the
        // folder-dedup rule fires, leaving only [Catalog].
        Assert.Equal(new[] { "Catalog" }, Breadcrumbs.BuildParents("/Catalog/Products/Index", "Products"));
    }

    [Fact]
    public void BuildParents_ActionPageWithDistinctTitle_KeepsContainingFolder()
    {
        // Catalog/Products/Detail: "Detail" is action leaf-dropped, but title != "Products" so the
        // folder-dedup does NOT fire — "Products" stays as a parent crumb.
        Assert.Equal(
            new[] { "Catalog", "Products" },
            Breadcrumbs.BuildParents("/Catalog/Products/Detail", "Chicken Thighs"));
    }

    [Fact]
    public void BuildParents_SingleSegmentPage_HasNoParents()
    {
        Assert.Empty(Breadcrumbs.BuildParents("/Today", "Today"));
    }

    [Fact]
    public void Icon_WithParents_UsesFirstParentIcon()
    {
        var parents = Breadcrumbs.BuildParents("/Catalog/Products/Index", "Products");
        var segments = "/Catalog/Products/Index".Split('/', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("tag", Breadcrumbs.Icon(parents, segments));
    }

    [Fact]
    public void Icon_SingleSegmentPage_FallsBackToFirstSegment()
    {
        // No parents (single-segment page), so the icon falls back to the first raw segment "Today".
        var segments = new[] { "Today" };
        Assert.Equal("sun", Breadcrumbs.Icon(Array.Empty<string>(), segments));
    }

    [Fact]
    public void Icon_UnknownSegment_ReturnsNull()
    {
        Assert.Null(Breadcrumbs.Icon(Array.Empty<string>(), new[] { "Unknown" }));
    }
}
