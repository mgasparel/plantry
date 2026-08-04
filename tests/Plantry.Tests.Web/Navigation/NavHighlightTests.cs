using Plantry.Web.Navigation;

namespace Plantry.Tests.Web.Navigation;

/// <summary>
/// L1 regression tests for <see cref="NavHighlight.IsActive"/> — the sidebar nav-highlight
/// predicate extracted from <c>_Layout.cshtml</c>. Guards the Pantry / Take&#160;Stock
/// carve-out (via the <c>except</c> prefix) that regressed once, plus prefix and
/// case-insensitivity semantics.
/// </summary>
public sealed class NavHighlightTests
{
    // Take Stock lives under the Pantry route tree, so the Pantry link carves it out via `except`.
    private const string PantryPrefix = "/Pantry";
    private const string TakeStockPrefix = "/Pantry/TakeStock";

    [Fact]
    public void OnTakeStock_TakeStockLinkIsActive()
    {
        Assert.True(NavHighlight.IsActive("/Pantry/TakeStock/Index", TakeStockPrefix));
    }

    [Fact]
    public void OnTakeStock_PantryLinkIsNotActive_BecauseOfExceptCarveOut()
    {
        // Without the `except` carve-out both links would light up on Take Stock — the bug this guards.
        Assert.False(NavHighlight.IsActive("/Pantry/TakeStock/Index", PantryPrefix, except: TakeStockPrefix));
    }

    [Fact]
    public void OnPantryIndex_PantryLinkIsActive()
    {
        Assert.True(NavHighlight.IsActive("/Pantry/Index", PantryPrefix, except: TakeStockPrefix));
    }

    [Fact]
    public void OnPantryIndex_TakeStockLinkIsNotActive()
    {
        Assert.False(NavHighlight.IsActive("/Pantry/Index", TakeStockPrefix));
    }

    [Fact]
    public void OnPantryNestedPage_PantryLinkStaysActive()
    {
        // A deeper Pantry-tree page (that is not under the Take Stock carve-out) keeps Pantry active.
        Assert.True(NavHighlight.IsActive("/Pantry/Products/Detail", PantryPrefix, except: TakeStockPrefix));
    }

    [Fact]
    public void Matching_IsCaseInsensitive()
    {
        // The `page` route value is the PascalCase Razor page name (e.g. /Pantry/TakeStock/Index);
        // only its casing may vary, and that must not affect the match.
        Assert.True(NavHighlight.IsActive("/pantry/index", PantryPrefix));
        Assert.True(NavHighlight.IsActive("/PANTRY/TAKESTOCK/Index", TakeStockPrefix));
    }

    [Fact]
    public void Except_IsAlsoCaseInsensitive()
    {
        Assert.False(NavHighlight.IsActive("/pantry/takestock/index", PantryPrefix, except: TakeStockPrefix));
    }

    [Fact]
    public void NullPage_IsNeverActive()
    {
        Assert.False(NavHighlight.IsActive(null, PantryPrefix));
    }

    [Fact]
    public void NonMatchingPrefix_IsNotActive()
    {
        Assert.False(NavHighlight.IsActive("/Catalog/Index", PantryPrefix));
    }

    [Fact]
    public void PageNotUnderExcept_RemainsActive()
    {
        // `except` only suppresses pages actually under it; an unrelated Pantry page is unaffected.
        Assert.True(NavHighlight.IsActive("/Pantry/Locations/Index", PantryPrefix, except: TakeStockPrefix));
    }

    // ── IsMoreActive (plantry-kdvi) ──────────────────────────────────────────────
    // The bottom-nav More button lights up on every destination the More sheet owns: /MealPlan,
    // /Deals, /Intake, /Pantry, /Catalog, /TidyUp, /Settings, /Dev, /Import.

    [Theory]
    [InlineData("/MealPlan/Index")]
    [InlineData("/Deals/Index")]
    [InlineData("/Intake/Upload")]
    [InlineData("/Pantry/Index")]
    [InlineData("/Pantry/TakeStock/Index")]
    [InlineData("/Catalog/Products/Index")]
    [InlineData("/TidyUp/Index")]
    [InlineData("/Settings/Index")]
    [InlineData("/Dev/Index")]
    [InlineData("/Import/Index")]
    public void IsMoreActive_OnAnyMorePrefix_IsActive(string page)
    {
        Assert.True(NavHighlight.IsMoreActive(page));
    }

    [Theory]
    [InlineData("/Today/Index")]
    [InlineData("/Recipes/Index")]
    [InlineData("/Shopping/Index")]
    public void IsMoreActive_OnBottomBarItems_IsNotActive(string page)
    {
        // Today, Recipes, and Shopping have their own bottom-nav items — More must not also light up.
        Assert.False(NavHighlight.IsMoreActive(page));
    }

    [Fact]
    public void IsMoreActive_MatchingIsCaseInsensitive()
    {
        Assert.True(NavHighlight.IsMoreActive("/deals/index"));
        Assert.True(NavHighlight.IsMoreActive("/TIDYUP/INDEX"));
    }

    [Fact]
    public void IsMoreActive_NullPage_IsNeverActive()
    {
        Assert.False(NavHighlight.IsMoreActive(null));
    }
}
