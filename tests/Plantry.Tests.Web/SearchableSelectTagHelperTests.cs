using System.Text.Encodings.Web;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Plantry.Web.TagHelpers;

namespace Plantry.Tests.Web;

/// <summary>
/// Unit tests for the ONE shared fuzzy-ranked search + create-button component (plantry-gzro.1) —
/// <see cref="SearchableSelectTagHelper"/>'s new unbound mode (<c>asp-for</c> omitted) and the
/// opt-in <c>AllowCreate</c> chrome (divider + demoted/full-strength button below the listbox).
///
/// <para>The pre-existing bound (<c>asp-for</c>) + <c>AllowCreate=false</c> mode is unchanged for
/// Shopping/Pantry and stays covered by their existing E2E journeys (<c>ShoppingJourneyTests</c>,
/// <c>StockSmokeTests</c>) — exercising it here would need a full <c>ModelExpression</c>/
/// <c>ViewContext</c>, which nothing in this codebase constructs outside the real MVC pipeline.</para>
/// </summary>
public sealed class SearchableSelectTagHelperTests
{
    private static readonly HtmlParser Parser = new();

    /// <summary>
    /// Invokes <see cref="SearchableSelectTagHelper.Process"/> directly and returns the emitted
    /// HTML. <c>htmlGenerator</c>/<see cref="TagHelperContext"/>.<c>ViewContext</c> are only
    /// dereferenced on the bound (<c>For</c> set) path, so the unbound-mode tests here don't need
    /// to construct them.
    /// </summary>
    private static string Render(SearchableSelectTagHelper helper, TagHelperAttributeList? unrecognizedAttributes = null)
    {
        var context = new TagHelperContext(
            allAttributes: new TagHelperAttributeList(),
            items: new Dictionary<object, object>(),
            uniqueId: "test");

        // Attributes remaining in output.Attributes simulate what the Razor-generated page class
        // passes through after stripping attributes bound to a recognized property (asp-for, items,
        // search-url, ...) — see SearchableSelectTagHelper.Process()'s pass-through loop.
        var output = new TagHelperOutput(
            "searchable-select",
            attributes: unrecognizedAttributes ?? new TagHelperAttributeList(),
            getChildContentAsync: (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

        helper.Process(context, output);

        // Process() sets output.TagName = null (the helper renders its own wrapper div), so the
        // emitted markup lives entirely in Content.
        return output.Content.GetContent(HtmlEncoder.Default);
    }

    [Fact(DisplayName = "Unbound mode (no asp-for) omits the hidden input but still renders the combobox")]
    public void Unbound_OmitsHiddenInput()
    {
        var helper = new SearchableSelectTagHelper(htmlGenerator: null!) { Placeholder = "Search…" };

        var doc = Parser.ParseDocument(Render(helper));

        Assert.Null(doc.QuerySelector("input[type=hidden]"));
        var input = doc.QuerySelector("input[type=text][role=combobox]");
        Assert.NotNull(input);
        Assert.Equal("Search…", input!.GetAttribute("placeholder"));
    }

    [Fact(DisplayName = "AllowCreate=false renders no divider, no create button, and no hasMatches wiring")]
    public void AllowCreateFalse_NoCreateChrome()
    {
        var helper = new SearchableSelectTagHelper(htmlGenerator: null!) { AllowCreate = false };

        var html = Render(helper);
        var doc = Parser.ParseDocument(html);

        Assert.Null(doc.QuerySelector("hr.searchable-select__create-divider"));
        Assert.Null(doc.QuerySelector("button"));
        Assert.DoesNotContain("product-search-create", html);
        Assert.DoesNotContain("hasMatches", html);
    }

    [Fact(DisplayName = "AllowCreate=true renders a divider and a create button directly below the listbox")]
    public void AllowCreateTrue_RendersDividerAndButtonBelowListbox()
    {
        var helper = new SearchableSelectTagHelper(htmlGenerator: null!)
        {
            AllowCreate = true,
            CreateLabel = "as a new grocery item",
        };

        var doc = Parser.ParseDocument(Render(helper));

        var root = doc.QuerySelector("div.searchable-select");
        Assert.NotNull(root);
        var children = root!.Children.ToList();

        var listboxIndex = children.FindIndex(c => c.ClassList.Contains("searchable-select__listbox"));
        var dividerIndex = children.FindIndex(c => c.ClassList.Contains("searchable-select__create-divider"));
        var buttonIndex = children.FindIndex(c => c.TagName.Equals("button", StringComparison.OrdinalIgnoreCase));

        Assert.True(listboxIndex >= 0, "listbox should render");
        Assert.True(dividerIndex > listboxIndex, "divider must come directly after the listbox");
        Assert.True(buttonIndex > dividerIndex, "create button must come directly after the divider");

        var button = children[buttonIndex];
        Assert.Contains("btn--ghost", button.ClassList);
        Assert.Contains("btn--sm", button.ClassList);
        Assert.Contains("product-search-create", button.GetAttribute("@click") ?? "");
        Assert.Contains("hasMatches", button.GetAttribute(":class") ?? "");
        Assert.Contains("btn--demoted", button.GetAttribute(":class") ?? "");
        // Demoted/full-strength toggle: btn--demoted is applied conditionally, not baked into the
        // static class list, so the plain "btn--ghost btn--sm" classes never carry it up front.
        Assert.DoesNotContain("btn--demoted", button.ClassList);
    }

    [Fact(DisplayName = "AllowCreate=true wires hasMatches from the listbox's htmx swap, independent of open")]
    public void AllowCreateTrue_WiresHasMatchesFromSwap()
    {
        var helper = new SearchableSelectTagHelper(htmlGenerator: null!) { AllowCreate = true };

        var doc = Parser.ParseDocument(Render(helper));
        var listbox = doc.QuerySelector("ul.searchable-select__listbox");

        Assert.NotNull(listbox);
        var swapHandler = listbox!.GetAttribute("@htmx:after-swap");
        Assert.NotNull(swapHandler);
        Assert.Contains("hasMatches", swapHandler);
        Assert.Contains("children.length > 0", swapHandler);
    }

    [Fact(DisplayName = "CreateLabel is HTML-encoded into data-create-label for Alpine to read")]
    public void CreateLabel_IsEncodedIntoDataAttribute()
    {
        var helper = new SearchableSelectTagHelper(htmlGenerator: null!)
        {
            AllowCreate = true,
            CreateLabel = "as a \"new\" product",
        };

        var doc = Parser.ParseDocument(Render(helper));
        var root = doc.QuerySelector("div.searchable-select");

        Assert.Equal("as a \"new\" product", root!.GetAttribute("data-create-label"));
    }

    [Fact(DisplayName = "Unbound mode with explicit Id derives the listbox id from it instead of a random guid (plantry-gzro.2)")]
    public void UnboundWithId_DerivesListboxIdFromExplicitId()
    {
        var helper = new SearchableSelectTagHelper(htmlGenerator: null!) { Id = "prod-search-sheet" };

        var doc = Parser.ParseDocument(Render(helper));
        var input = doc.QuerySelector("input[role=combobox]");
        var listbox = doc.QuerySelector("ul.searchable-select__listbox");

        Assert.Equal("prod-search-sheet-listbox", listbox!.GetAttribute("id"));
        Assert.Equal("prod-search-sheet-listbox", input!.GetAttribute("aria-controls"));
    }

    [Fact(DisplayName = "Unrecognized attributes on <searchable-select> pass through onto the rendered wrapper div (plantry-gzro.2)")]
    public void UnrecognizedAttribute_PassesThroughOntoWrapperDiv()
    {
        var helper = new SearchableSelectTagHelper(htmlGenerator: null!);
        var passthrough = new TagHelperAttributeList
        {
            new TagHelperAttribute("@sheet-product-set.window", "query = $event.detail; open = false; highlighted = -1"),
        };

        var doc = Parser.ParseDocument(Render(helper, passthrough));
        var root = doc.QuerySelector("div.searchable-select");

        Assert.Equal(
            "query = $event.detail; open = false; highlighted = -1",
            root!.GetAttribute("@sheet-product-set.window"));
    }
}
