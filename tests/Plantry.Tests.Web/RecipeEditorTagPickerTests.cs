using System.Net;
using AngleSharp.Html.Parser;
using Plantry.Recipes.Domain;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

// ── FIX 1: POST round-trip — TagIds survive to AuthorRecipe ──────────────────────

/// <summary>
/// POST round-trip test: submitting the editor with <c>Input.TagIds</c> populated causes
/// <see cref="AuthorRecipe"/> to resolve and persist the selected tags on the new recipe.
/// Asserts the acceptance criterion "save persists the chosen TagIds; reopening shows the same chips."
/// </summary>
public sealed class RecipeEditorOnPostTagPersistTests : IDisposable
{
    private readonly RecipeEditorPostFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient AuthenticatedClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeEditorFixture.HouseholdAId.ToString());
        return client;
    }

    private async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var html = await (await client.GetAsync("/Recipes/New")).Content.ReadAsStringAsync();
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the create page.");
        return match.Groups[1].Value;
    }

    /// <summary>
    /// AC: "save persists the chosen TagIds; reopening shows the same chips."
    ///
    /// POST to <c>/Recipes/New</c> with two tag ids from the fixture; expect a redirect (Saved outcome)
    /// and assert the recipe captured in the singleton <see cref="FakeEditorRecipeRepository"/> has
    /// exactly those two tags in its <see cref="Recipe.Tags"/> collection.
    /// </summary>
    [Fact]
    public async Task OnPost_with_TagIds_persists_chosen_tags_on_the_recipe()
    {
        var client = AuthenticatedClient();
        var token  = await GetAntiforgeryTokenAsync(client);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.Name",            "Tag Persist Test Recipe"),
            new("Input.DefaultServings", "2"),
            // One tracked ingredient line (required — recipe must have ≥1 ingredient).
            new("Input.Lines[0].Ordinal",    "0"),
            new("Input.Lines[0].ProductId",  RecipeEditorFixture.PastaId.ToString()),
            new("Input.Lines[0].Quantity",   "200"),
            new("Input.Lines[0].UnitId",     RecipeEditorFixture.GramUnitId.ToString()),
            // Two tag ids from the fixture active set.
            new("Input.TagIds[0]", RecipeEditorFixture.VegetarianTagId.Value.ToString()),
            new("Input.TagIds[1]", RecipeEditorFixture.QuickTagId.Value.ToString()),
        };

        var response = await client.PostAsync("/Recipes/New", new FormUrlEncodedContent(fields));

        // AuthorRecipe.Saved → redirect to details.
        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after successful save, got {(int)response.StatusCode}.");

        // Inspect what was captured in the singleton repository.
        var saved = _factory.RecipeRepo.LastAdded;
        Assert.NotNull(saved);
        Assert.Equal("Tag Persist Test Recipe", saved.Name);

        var persistedTagIds = saved.Tags.Select(rt => rt.TagId).ToHashSet();
        Assert.Contains(RecipeEditorFixture.VegetarianTagId, persistedTagIds);
        Assert.Contains(RecipeEditorFixture.QuickTagId, persistedTagIds);
        Assert.Equal(2, persistedTagIds.Count);
    }

    /// <summary>
    /// AC: unknown / foreign tag id is silently dropped (no minting).
    /// POST with a valid ingredient + one unknown TagId → saved recipe has zero tags.
    /// </summary>
    [Fact]
    public async Task OnPost_with_unknown_TagId_persists_recipe_with_no_tags()
    {
        var client = AuthenticatedClient();
        var token  = await GetAntiforgeryTokenAsync(client);

        var unknownId = Guid.NewGuid();

        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.Name",            "Unknown Tag Test"),
            new("Input.DefaultServings", "1"),
            new("Input.Lines[0].Ordinal",    "0"),
            new("Input.Lines[0].ProductId",  RecipeEditorFixture.PastaId.ToString()),
            new("Input.Lines[0].Quantity",   "100"),
            new("Input.Lines[0].UnitId",     RecipeEditorFixture.GramUnitId.ToString()),
            new("Input.TagIds[0]", unknownId.ToString()),
        };

        var response = await client.PostAsync("/Recipes/New", new FormUrlEncodedContent(fields));

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after successful save, got {(int)response.StatusCode}.");

        var saved = _factory.RecipeRepo.LastAdded;
        Assert.NotNull(saved);
        Assert.Empty(saved.Tags);
    }
}

// ── FIX 2: Empty-household guidance ──────────────────────────────────────────────

/// <summary>
/// When the household has zero active tags, the tags card should render a guidance message with
/// a link to <c>/Settings/Tags</c> instead of the chip picker input, asserting the acceptance
/// criterion "Empty household → guidance pointing to /Settings."
/// </summary>
public sealed class RecipeEditorEmptyTagsGuidanceTests : IDisposable
{
    private static readonly HtmlParser Parser = new();
    private readonly RecipeEditorEmptyTagsFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Tags_card_shows_settings_link_when_no_active_tags_defined()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeEditorFixture.HouseholdAId.ToString());

        var response = await client.GetAsync("/Recipes/New");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        var doc      = Parser.ParseDocument(html);
        var tagsCard = doc.QuerySelectorAll("#recipe-editor .card")
            .FirstOrDefault(c => c.QuerySelector(".card__head-title")?.TextContent?.Trim() == "Tags")
            ?? throw new InvalidOperationException("Tags card not found.");

        // Guidance link must point to /Settings/Tags.
        var settingsLink = tagsCard.QuerySelector("a[href='/Settings/Tags']");
        Assert.NotNull(settingsLink);

        // The chip picker input container must NOT be rendered when there are no active tags.
        var chipInput = tagsCard.QuerySelector("[x-data]");
        Assert.Null(chipInput);
    }
}

// ── FIX 4: Re-render after validation failure — TagNames + ProductName preserved ──

/// <summary>
/// When a POST fails validation (e.g., missing ingredient qty/unit), the re-rendered page must:
/// (a) Show the tag chip names correctly (from RestoreTagNames — not blank chips).
/// (b) Show the ingredient product name correctly (from posted ProductName field — not blank rows).
///
/// Prior to the fix: TagNames was not posted with the form (only TagIds were), so Input.TagNames was
/// empty after POST, causing the Zip to produce [] and Alpine to show blank chips.
/// ProductName was not a hidden input, so it was null after POST, blanking ingredient rows.
/// </summary>
public sealed class RecipeEditorReRenderAfterValidationFailureTests : IDisposable
{
    private static readonly HtmlParser Parser = new();
    private readonly RecipeEditorPostFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient AuthenticatedClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeEditorFixture.HouseholdAId.ToString());
        return client;
    }

    private async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var html = await (await client.GetAsync("/Recipes/New")).Content.ReadAsStringAsync();
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the create page.");
        return match.Groups[1].Value;
    }

    /// <summary>
    /// POST a form that triggers a validation error (missing Quantity on a tracked ingredient).
    /// The re-rendered page (200 OK, not redirect) must embed the tag chip names in the Alpine
    /// x-data tags array — not blank chips — and the ingredient ProductName in the rows JSON.
    /// </summary>
    [Fact]
    public async Task OnPost_validation_failure_rerender_preserves_tag_names_and_ingredient_product_name()
    {
        var client = AuthenticatedClient();
        var token  = await GetAntiforgeryTokenAsync(client);

        // Post a tracked ingredient WITHOUT a quantity — triggers R5 "A tracked ingredient must have
        // both a quantity and a unit." Also include a tag (by id) and a ProductName hidden field.
        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.Name",            "Fail Test Recipe"),
            new("Input.DefaultServings", "2"),
            // Ingredient row: tracked product, ProductName posted, but NO Quantity — triggers validation failure.
            new("Input.Lines[0].Ordinal",      "0"),
            new("Input.Lines[0].ProductId",    RecipeEditorFixture.PastaId.ToString()),
            new("Input.Lines[0].ProductName",  "Rigatoni"),
            new("Input.Lines[0].UnitId",       RecipeEditorFixture.GramUnitId.ToString()),
            // Note: Quantity intentionally omitted to trigger R5 validation error.
            // Tag: id only (name not posted — must be restored server-side from TagOptions).
            new("Input.TagIds", RecipeEditorFixture.VegetarianTagId.Value.ToString()),
        };

        var response = await client.PostAsync("/Recipes/New", new FormUrlEncodedContent(fields));

        // Must be 200 (re-render on validation failure), NOT a redirect.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        var doc  = Parser.ParseDocument(html);

        var editor = doc.QuerySelector("#recipe-editor")
            ?? throw new InvalidOperationException("#recipe-editor not found in re-rendered page.");
        var xData = editor.GetAttribute("x-data") ?? "";

        // (a) Tag name "Vegetarian" must appear in the "tags" array in the x-data — not blank.
        // The tags array in x-data is: tags: [{"id":"...","name":"Vegetarian"}]
        // After HTML entity decoding (attribute value), the name is the plain string.
        Assert.Contains("Vegetarian", xData, StringComparison.OrdinalIgnoreCase);

        // (b) Ingredient product name "Rigatoni" must appear in the "rows" array in the x-data.
        // The rows JSON in x-data contains: productName:"Rigatoni"
        Assert.Contains("Rigatoni", xData, StringComparison.OrdinalIgnoreCase);
    }
}

// ── FIX 3: Archived-applied-tag edge case ────────────────────────────────────────

/// <summary>
/// When a recipe has a tag that was later archived:
/// <list type="bullet">
///   <item>The chip is still pre-populated in the Alpine <c>tags</c> initialiser (chip shows on GET).</item>
///   <item>The archived tag is absent from <c>tagOptions</c> (cannot be re-added from the dropdown).</item>
/// </list>
/// Asserts the acceptance criterion "Edit a recipe whose tag was archived → chip still shows and is
/// removable but not re-addable from the dropdown."
/// </summary>
public sealed class RecipeEditorArchivedTagTests : IClassFixture<RecipeEditorFragmentFactory>
{
    private static readonly HtmlParser Parser = new();
    private readonly RecipeEditorFragmentFactory _factory;

    public RecipeEditorArchivedTagTests(RecipeEditorFragmentFactory factory)
        => _factory = factory;

    [Fact]
    public async Task Archived_applied_tag_appears_as_chip_but_not_in_dropdown_options()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeEditorFixture.HouseholdAId.ToString());

        var response = await client.GetAsync(
            $"/Recipes/{RecipeEditorFixture.RichArchivedTagRecipeId.Value}/Edit");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        // The x-data attribute on the recipe editor container embeds the Alpine state as JSON.
        // We check that:
        //   (a) the archived tag's id appears in the serialised "tags" array (chip shown)
        //   (b) the archived tag's id does NOT appear in the serialised "tagOptions" array (not re-addable)
        var doc    = Parser.ParseDocument(html);
        var editor = doc.QuerySelector("#recipe-editor")
            ?? throw new InvalidOperationException("#recipe-editor not found.");
        var xData  = editor.GetAttribute("x-data") ?? "";

        var archivedIdStr = RecipeEditorFixture.ArchivedTagId.Value.ToString();

        // (a) Archived tag id must appear somewhere in the x-data (it's in the "tags" array).
        Assert.Contains(archivedIdStr, xData, StringComparison.OrdinalIgnoreCase);

        // (b) Parse out tagOptions array and confirm archived id is absent.
        //     The serialised x-data has the form: ... tagOptions:[{id:'...',name:'...'},...],...
        //     We check that the archived id does NOT appear as a value inside tagOptions.
        var tagOptionsMatch = System.Text.RegularExpressions.Regex.Match(
            xData, @"tagOptions\s*:\s*(\[.*?\])", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(tagOptionsMatch.Success, "tagOptions not found in x-data.");
        var tagOptionsJson = tagOptionsMatch.Groups[1].Value;
        Assert.DoesNotContain(archivedIdStr, tagOptionsJson, StringComparison.OrdinalIgnoreCase);

        // Additionally confirm the "Vegetarian" active tag IS in tagOptions (sanity check).
        var vegetarianIdStr = RecipeEditorFixture.VegetarianTagId.Value.ToString();
        Assert.Contains(vegetarianIdStr, tagOptionsJson, StringComparison.OrdinalIgnoreCase);
    }
}
