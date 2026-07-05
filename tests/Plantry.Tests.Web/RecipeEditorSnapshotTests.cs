using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 fragment snapshot tests for the recipe editor page (J6 create + J7 edit). Each test
/// fetches the real editor page as household A via the <see cref="RecipeEditorFragmentFactory"/>
/// WAF, extracts one fragment (the full editor container, the tag-editor card, the scale-offer
/// card, the ingredients card, or the commit bar), and verifies it against a committed baseline.
///
/// <para>Because the editor page uses Alpine <c>x-for</c> template loops for ingredient rows
/// (client-side rendering), the server-rendered HTML captures the initial <c>x-data</c> JSON
/// initialiser and the static card structure — not per-row markup. The snapshots therefore
/// assert: (a) correct JSON serialisation of pre-populated lines (product names, quantities,
/// group headings, untracked flags) in edit mode, (b) correct tag chips rendered in the
/// tag card, (c) the scale-offer card is present in the DOM (visibility controlled by Alpine),
/// and (d) the page structure stays stable as later changes land.</para>
///
/// <para>Create-mode tests request <c>/Recipes/Edit</c> (no id route segment).
/// Edit-mode tests request <c>/Recipes/{id}/Edit</c> with the fixture recipe id.</para>
/// </summary>
public sealed class RecipeEditorSnapshotTests(RecipeEditorFragmentFactory factory)
    : IClassFixture<RecipeEditorFragmentFactory>
{
    private static readonly HtmlParser Parser = new();

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private async Task<string> GetCreatePageAsync()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeEditorFixture.HouseholdAId.ToString());
        // The create-form route is "/Recipes/New" (registered via AddPageRoute convention in Program.cs).
        // The page-level template "/Recipes/{id:guid?}/Edit" does not collapse to "/Recipes/Edit"
        // without a GUID segment in ASP.NET Core's endpoint routing.
        var response = await client.GetAsync("/Recipes/New");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<string> GetEditPageAsync(Guid recipeId)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeEditorFixture.HouseholdAId.ToString());
        var response = await client.GetAsync($"/Recipes/{recipeId}/Edit");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static string Extract(string pageHtml, string selector)
    {
        var doc = Parser.ParseDocument(pageHtml);
        var element = doc.QuerySelector(selector)
            ?? throw new InvalidOperationException($"Selector '{selector}' not found in page HTML.");
        return Pretty(element);
    }

    private static string Pretty(IElement element)
    {
        using var writer = new StringWriter();
        element.ToHtml(writer, new PrettyMarkupFormatter());
        return writer.ToString().Replace("\r\n", "\n").Trim();
    }

    // ── Create mode ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Full editor container in create mode (no id route segment). Asserts the x-data initialiser
    /// carries an empty rows array with one blank row, tags:[], origServings:0, and isCreate:true.
    /// </summary>
    [Fact]
    public async Task Editor_create_full()
    {
        var html = await GetCreatePageAsync();
        await Verify(Extract(html, "#recipe-editor"), "html");
    }

    /// <summary>
    /// The ingredients card in create mode — one blank Alpine template row, the "Add ingredient" button.
    /// </summary>
    [Fact]
    public async Task Editor_create_ingredients_card()
    {
        var html = await GetCreatePageAsync();
        // The ingredients card is the third .card inside #recipe-editor (details / tags / ingredients / directions).
        var doc = Parser.ParseDocument(html);
        var cards = doc.QuerySelectorAll("#recipe-editor .card");
        // Pick the card whose head title says "Ingredients"
        var ingredientsCard = cards.FirstOrDefault(c => c.QuerySelector(".card__head-title")?.TextContent?.Trim() == "Ingredients")
            ?? throw new InvalidOperationException("Ingredients card not found.");
        await Verify(Pretty(ingredientsCard), "html");
    }

    /// <summary>
    /// Unauthenticated request to the create page is challenged (redirect or 401).
    /// The route "/Recipes/New" is the registered create alias (see AddPageRoute in Program.cs).
    /// </summary>
    [Fact]
    public async Task Editor_create_unauthenticated_is_challenged()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var response = await client.GetAsync("/Recipes/New");
        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized,
            $"Expected redirect or 401, got {(int)response.StatusCode}");
    }

    // ── Edit mode — empty recipe ──────────────────────────────────────────────────

    /// <summary>
    /// Full editor container in edit mode for the empty fixture recipe. Asserts origServings is
    /// pre-populated from the recipe, rows array contains one blank row (RestoreLines fallback),
    /// and isCreate:false.
    /// </summary>
    [Fact]
    public async Task Editor_edit_empty_recipe_full()
    {
        var html = await GetEditPageAsync(factory.EmptyRecipe.Id.Value);
        await Verify(Extract(html, "#recipe-editor"), "html");
    }

    // ── Photo preview (plantry-nj0e) ──────────────────────────────────────────────

    private const string PhotoPreviewSelector = "a[href$='handler=Photo'] img";

    /// <summary>
    /// Editing a recipe that HAS a photo renders a thumbnail preview wrapped in an anchor that opens the
    /// full image (href → the Photo handler, opened in a new tab). Pins the primary acceptance criterion.
    /// </summary>
    [Fact]
    public async Task Editor_edit_recipe_with_photo_shows_clickable_preview()
    {
        var html = await GetEditPageAsync(factory.PhotoRecipe.Id.Value);
        var doc = Parser.ParseDocument(html);

        var previewImg = doc.QuerySelector(PhotoPreviewSelector);
        Assert.NotNull(previewImg);

        // The wrapping anchor points at the Photo handler and opens the full image in a new tab.
        var anchor = previewImg!.Closest("a");
        Assert.NotNull(anchor);
        Assert.Equal($"/Recipes/{factory.PhotoRecipe.Id.Value}?handler=Photo", anchor!.GetAttribute("href"));
        Assert.Equal("_blank", anchor.GetAttribute("target"));
        // The thumbnail itself is served from the same Photo handler.
        Assert.Equal($"/Recipes/{factory.PhotoRecipe.Id.Value}?handler=Photo", previewImg.GetAttribute("src"));
    }

    /// <summary>
    /// Editing a photoless recipe shows NO preview (the other half of the acceptance criterion).
    /// </summary>
    [Fact]
    public async Task Editor_edit_recipe_without_photo_shows_no_preview()
    {
        var html = await GetEditPageAsync(factory.EmptyRecipe.Id.Value);
        var doc = Parser.ParseDocument(html);
        Assert.Null(doc.QuerySelector(PhotoPreviewSelector));
    }

    /// <summary>
    /// Create mode shows NO photo preview (there is no recipe id / photo yet).
    /// </summary>
    [Fact]
    public async Task Editor_create_shows_no_photo_preview()
    {
        var html = await GetCreatePageAsync();
        var doc = Parser.ParseDocument(html);
        Assert.Null(doc.QuerySelector(PhotoPreviewSelector));
    }

    // ── Edit mode — rich recipe ───────────────────────────────────────────────────

    /// <summary>
    /// Full editor container in edit mode for the rich fixture recipe. The x-data serialised JSON
    /// must carry all five ingredient rows (two groups, one untracked staple), both tags, and the
    /// correct origServings / hasIngredients values.
    /// </summary>
    [Fact]
    public async Task Editor_edit_rich_recipe_full()
    {
        var html = await GetEditPageAsync(factory.RichRecipe.Id.Value);
        await Verify(Extract(html, "#recipe-editor"), "html");
    }

    /// <summary>
    /// Tags card in edit mode — the x-data tags array carries ["Vegetarian","Quick"] pre-populated
    /// from the fixture recipe's tag set, rendered as chip hidden-inputs in the Alpine template.
    /// </summary>
    [Fact]
    public async Task Editor_edit_rich_tags_card()
    {
        var html = await GetEditPageAsync(factory.RichRecipe.Id.Value);
        var doc = Parser.ParseDocument(html);
        var tagsCard = doc.QuerySelectorAll("#recipe-editor .card")
            .FirstOrDefault(c => c.QuerySelector(".card__head-title")?.TextContent?.Trim() == "Tags")
            ?? throw new InvalidOperationException("Tags card not found.");
        await Verify(Pretty(tagsCard), "html");
    }

    /// <summary>
    /// Ingredients card in edit mode — the unit options in the select elements must reflect the
    /// fixture unit set (g, ea). The rows JSON in x-data includes group headings, tracked flags,
    /// and the untracked staple row (isUntracked would be set client-side; the server serialises
    /// newStapleName: null for all rows in the initial render).
    /// </summary>
    [Fact]
    public async Task Editor_edit_rich_ingredients_card()
    {
        var html = await GetEditPageAsync(factory.RichRecipe.Id.Value);
        var doc = Parser.ParseDocument(html);
        var ingredientsCard = doc.QuerySelectorAll("#recipe-editor .card")
            .FirstOrDefault(c => c.QuerySelector(".card__head-title")?.TextContent?.Trim() == "Ingredients")
            ?? throw new InvalidOperationException("Ingredients card not found.");
        await Verify(Pretty(ingredientsCard), "html");
    }

    /// <summary>
    /// The servings-scale offer card is present in the DOM in edit mode (Alpine controls visibility
    /// via x-show="showScaleOffer()", which evaluates client-side). The server always emits the card
    /// for non-create mode; the snapshot asserts the card's structure and the two radio inputs.
    /// </summary>
    [Fact]
    public async Task Editor_edit_scale_offer_card()
    {
        var html = await GetEditPageAsync(factory.RichRecipe.Id.Value);
        // The scale-offer card has x-show="showScaleOffer()" and x-cloak; it is always emitted
        // server-side. Select by the seg-ctrl inside it (unique to the scale card).
        var doc = Parser.ParseDocument(html);
        var scaleCard = doc.QuerySelector(".seg-ctrl[role='group'][aria-label='Scale mode']")
            ?.Closest(".card")
            ?? throw new InvalidOperationException("Scale-offer card not found.");
        await Verify(Pretty(scaleCard), "html");
    }

    /// <summary>
    /// The submit bar in edit mode — should read "Save changes" (not "Create recipe").
    /// </summary>
    [Fact]
    public async Task Editor_edit_submit_bar()
    {
        var html = await GetEditPageAsync(factory.RichRecipe.Id.Value);
        await Verify(Extract(html, ".commit-bar"), "html");
    }

    /// <summary>
    /// The submit bar in create mode — should read "Create recipe".
    /// </summary>
    [Fact]
    public async Task Editor_create_submit_bar()
    {
        var html = await GetCreatePageAsync();
        await Verify(Extract(html, ".commit-bar"), "html");
    }

    /// <summary>
    /// Foreign-household request for the rich recipe returns 404 (the fake repository
    /// filters by household id, mirroring the real RLS + EF query filter).
    /// </summary>
    [Fact]
    public async Task Editor_edit_foreign_household_returns_notfound()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            "bbbbbbbb-0000-0000-0000-000000000002");
        var response = await client.GetAsync($"/Recipes/{factory.RichRecipe.Id.Value}/Edit");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
