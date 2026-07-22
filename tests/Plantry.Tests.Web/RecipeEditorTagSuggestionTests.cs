using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// Integration tests for the recipe editor's edit-moment AI tag suggestions (plantry-qll2.2), driven
/// through the real <c>Plantry.Web</c> pipeline with the LLM seam (<see cref="IRecipeTagSuggester"/>)
/// and the assistive-AI gate (<see cref="IAiAssistanceGateReader"/>) faked. Proves the three acceptance
/// criteria at the page-model/handler layer:
/// <list type="number">
///   <item>Creating a recipe surfaces plausible chips from household vocab (existing + new).</item>
///   <item>Ignoring all chips leaves the recipe exactly as manual tagging would.</item>
///   <item>Toggle off ⇒ no call, no chips.</item>
/// </list>
/// Plus the new-tag accept path: an accepted <c>.ai-chip--new</c> is minted and applied on save.
/// </summary>
public sealed class RecipeEditorTagSuggestionTests
{
    private static readonly Guid HouseholdAId = RecipeEditorFixture.HouseholdAId;

    // Canned suggestions the stub suggester returns: one existing-vocab match + one new-tag proposal.
    private static IReadOnlyList<TagSuggestion> CannedSuggestions() =>
    [
        new("Vegetarian", null, RecipeEditorFixture.VegetarianTagId.Value), // existing household tag
        new("Dairy-Free", TagCategory.Diet, ExistingTagId: null),           // would mint a new tag
    ];

    private static HttpClient AuthedClient(WebApplicationFactory<Program> f)
    {
        var client = f.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdAId.ToString());
        return client;
    }

    private static async Task<string> AntiforgeryTokenAsync(HttpClient client)
    {
        var html = await (await client.GetAsync("/Recipes/New")).Content.ReadAsStringAsync();
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the create page.");
        return match.Groups[1].Value;
    }

    // ── Criterion 1: chips surface from household vocab (existing + new) ──────────

    [Fact]
    public async Task SuggestTags_gate_on_returns_existing_and_new_chips()
    {
        using var factory = new TagSuggestionFactory(gateEnabled: true, CannedSuggestions());
        var client = AuthedClient(factory);

        var response = await client.GetAsync(
            $"/Recipes/New?handler=SuggestTags&productIds={RecipeEditorFixture.PastaId}&productIds={RecipeEditorFixture.TomatoId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var suggestions = doc.RootElement.GetProperty("suggestions").EnumerateArray().ToList();
        Assert.Equal(2, suggestions.Count);

        var existing = suggestions.Single(s => s.GetProperty("name").GetString() == "Vegetarian");
        Assert.False(existing.GetProperty("isNew").GetBoolean());
        Assert.Equal(RecipeEditorFixture.VegetarianTagId.Value.ToString(), existing.GetProperty("existingTagId").GetString());

        var neu = suggestions.Single(s => s.GetProperty("name").GetString() == "Dairy-Free");
        Assert.True(neu.GetProperty("isNew").GetBoolean());
        Assert.Equal("Diet", neu.GetProperty("category").GetString());
        // A new-tag proposal carries no existing id — it mints only on the user's tap.
        Assert.Equal(JsonValueKind.Null, neu.GetProperty("existingTagId").ValueKind);
    }

    // ── Criterion 3: toggle off ⇒ no call, no chips ──────────────────────────────

    [Fact]
    public async Task SuggestTags_gate_off_returns_no_chips_and_never_calls_the_suggester()
    {
        using var factory = new TagSuggestionFactory(gateEnabled: false, CannedSuggestions());
        var client = AuthedClient(factory);

        var response = await client.GetAsync(
            $"/Recipes/New?handler=SuggestTags&productIds={RecipeEditorFixture.PastaId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        Assert.Empty(doc.RootElement.GetProperty("suggestions").EnumerateArray());
        Assert.False(factory.Suggester.WasCalled);
    }

    [Fact]
    public async Task SuggestTags_with_no_product_ids_returns_no_chips()
    {
        using var factory = new TagSuggestionFactory(gateEnabled: true, CannedSuggestions());
        var client = AuthedClient(factory);

        var response = await client.GetAsync("/Recipes/New?handler=SuggestTags");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Empty(doc.RootElement.GetProperty("suggestions").EnumerateArray());
    }

    // ── plantry-crre: applied tag names reach the suggester, and redundant exact matches are dropped ──

    [Fact]
    public async Task SuggestTags_passes_applied_tag_names_through_to_the_suggester()
    {
        using var factory = new TagSuggestionFactory(gateEnabled: true, CannedSuggestions());
        var client = AuthedClient(factory);

        var response = await client.GetAsync(
            $"/Recipes/New?handler=SuggestTags&productIds={RecipeEditorFixture.PastaId}"
            + "&appliedTagNames=Vegan&appliedTagNames=Quick");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(factory.Suggester.LastAppliedTagNames);
        Assert.Equal(["Vegan", "Quick"], factory.Suggester.LastAppliedTagNames);
    }

    [Fact]
    public async Task SuggestTags_drops_a_suggestion_whose_name_exactly_matches_an_applied_tag()
    {
        // The stub suggester always returns "Vegetarian" + "Dairy-Free" regardless of input; the handler
        // must still strip "Vegetarian" from the response when the client says it's already applied — a
        // defense-in-depth backstop independent of whatever the LLM itself honours.
        using var factory = new TagSuggestionFactory(gateEnabled: true, CannedSuggestions());
        var client = AuthedClient(factory);

        var response = await client.GetAsync(
            $"/Recipes/New?handler=SuggestTags&productIds={RecipeEditorFixture.PastaId}"
            + "&appliedTagNames=Vegetarian");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var suggestions = doc.RootElement.GetProperty("suggestions").EnumerateArray().ToList();

        Assert.DoesNotContain(suggestions, s => s.GetProperty("name").GetString() == "Vegetarian");
        Assert.Contains(suggestions, s => s.GetProperty("name").GetString() == "Dairy-Free");
    }

    // ── New-tag accept path: minted + applied on save ────────────────────────────

    [Fact]
    public async Task OnPost_accepted_new_tag_chip_is_minted_and_applied_to_the_recipe()
    {
        using var factory = new TagSuggestionFactory(gateEnabled: true, CannedSuggestions());
        var client = AuthedClient(factory);
        var token = await AntiforgeryTokenAsync(client);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.Name",            "New Tag Mint Test"),
            new("Input.DefaultServings", "2"),
            new("Input.Lines[0].Ordinal",   "0"),
            new("Input.Lines[0].ProductId", RecipeEditorFixture.PastaId.ToString()),
            new("Input.Lines[0].Quantity",  "200"),
            new("Input.Lines[0].UnitId",    RecipeEditorFixture.GramUnitId.ToString()),
            // The user tapped the dashed new-tag chip — posted for minting on save.
            new("Input.NewTags[0].Name",     "Dairy-Free"),
            new("Input.NewTags[0].Category", "Diet"),
        };

        var response = await client.PostAsync("/Recipes/New", new FormUrlEncodedContent(fields));
        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after successful save, got {(int)response.StatusCode}.");

        // The tag was minted into the household vocabulary…
        var minted = factory.TagRepo.Items.SingleOrDefault(t => t.Name == "Dairy-Free");
        Assert.NotNull(minted);
        Assert.Equal(TagCategory.Diet, minted!.Category);

        // …and applied to the saved recipe (resolved by AuthorRecipe from its freshly-minted id).
        var saved = factory.RecipeRepo.LastAdded;
        Assert.NotNull(saved);
        Assert.Contains(minted.Id, saved!.Tags.Select(rt => rt.TagId));
    }

    // ── Criterion 2: ignoring chips leaves the recipe as manual tagging would ─────

    [Fact]
    public async Task OnPost_without_accepted_suggestions_saves_only_the_manually_chosen_tags()
    {
        using var factory = new TagSuggestionFactory(gateEnabled: true, CannedSuggestions());
        var client = AuthedClient(factory);
        var token = await AntiforgeryTokenAsync(client);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.Name",            "Ignore Suggestions Test"),
            new("Input.DefaultServings", "2"),
            new("Input.Lines[0].Ordinal",   "0"),
            new("Input.Lines[0].ProductId", RecipeEditorFixture.PastaId.ToString()),
            new("Input.Lines[0].Quantity",  "200"),
            new("Input.Lines[0].UnitId",    RecipeEditorFixture.GramUnitId.ToString()),
            // One manually-picked tag; NO NewTags — the suggestion chips were ignored.
            new("Input.TagIds[0]", RecipeEditorFixture.QuickTagId.Value.ToString()),
        };

        var response = await client.PostAsync("/Recipes/New", new FormUrlEncodedContent(fields));
        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after successful save, got {(int)response.StatusCode}.");

        var saved = factory.RecipeRepo.LastAdded;
        Assert.NotNull(saved);
        // Exactly the manual tag — no suggestion leaked in, and nothing new was minted.
        var tagIds = saved!.Tags.Select(rt => rt.TagId).ToHashSet();
        Assert.Equal([RecipeEditorFixture.QuickTagId], tagIds);
        Assert.DoesNotContain(factory.TagRepo.Items, t => t.Name == "Dairy-Free");
    }
}

// ── WAF + fakes ──────────────────────────────────────────────────────────────────

/// <summary>
/// WAF for the tag-suggestion editor tests. Fakes the assistive-AI gate and the LLM suggester behind the
/// Recipes ACL ports, uses a mutable tag repository so the mint-on-save round-trip is real, and exposes
/// the recipe + tag repositories so tests can assert what was persisted.
/// </summary>
internal sealed class TagSuggestionFactory : WebApplicationFactory<Program>
{
    private readonly bool _gateEnabled;
    private readonly IReadOnlyList<TagSuggestion> _canned;

    public MutableTagRepository TagRepo { get; }
    public FakeEditorRecipeRepository RecipeRepo { get; }
    public StubTagSuggester Suggester { get; }

    public TagSuggestionFactory(bool gateEnabled, IReadOnlyList<TagSuggestion> canned)
    {
        _gateEnabled = gateEnabled;
        _canned = canned;
        TagRepo = new MutableTagRepository(RecipeEditorFixture.ActiveTags());
        RecipeRepo = new FakeEditorRecipeRepository(new ConstantTenantContext(RecipeEditorFixture.HouseholdAId));
        Suggester = new StubTagSuggester(_canned);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.AddFakeExpiringSoonHorizon();
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.RemoveAll<IRecipeRepository>();
            services.AddSingleton<IRecipeRepository>(RecipeRepo);

            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(TagRepo);

            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeEditorProductReader(
                    RecipeEditorFixture.ProductSummaries(),
                    RecipeEditorFixture.UnitCodes(),
                    RecipeEditorFixture.UnitOptions(),
                    RecipeEditorFixture.ProductDefaultUnits()));

            services.RemoveAll<ICatalogWriter>();
            services.AddSingleton<ICatalogWriter>(new FakeCatalogWriter());

            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeUnitConverter());

            // The two plantry-qll2.2 seams: the assistive-AI gate and the untrusted LLM suggester.
            services.RemoveAll<IAiAssistanceGateReader>();
            services.AddSingleton<IAiAssistanceGateReader>(new StubGateReader(_gateEnabled));

            services.RemoveAll<IRecipeTagSuggester>();
            services.AddSingleton<IRecipeTagSuggester>(Suggester);
        });
    }
}

/// <summary>Fake <see cref="IAiAssistanceGateReader"/> returning a fixed toggle state.</summary>
internal sealed class StubGateReader(bool enabled) : IAiAssistanceGateReader
{
    public Task<bool> IsEnabledAsync(CancellationToken ct = default) => Task.FromResult(enabled);
}

/// <summary>Fake <see cref="IRecipeTagSuggester"/> returning canned proposals and recording whether it ran.</summary>
internal sealed class StubTagSuggester(IReadOnlyList<TagSuggestion> canned) : IRecipeTagSuggester
{
    public bool WasCalled { get; private set; }
    public IReadOnlyList<string>? LastAppliedTagNames { get; private set; }

    public Task<IReadOnlyList<TagSuggestion>> SuggestAsync(
        IReadOnlyList<string> ingredientNames,
        IReadOnlyList<TagVocabularyEntry> vocabulary,
        IReadOnlyList<string> appliedTagNames,
        CancellationToken ct = default)
    {
        WasCalled = true;
        LastAppliedTagNames = appliedTagNames;
        return Task.FromResult(canned);
    }
}

/// <summary>
/// Mutable in-memory <see cref="ITagRepository"/> that supports the full mint round-trip: seeded active
/// tags plus tags added via <see cref="AddAsync"/> (ManageTagsService.CreateAsync), so
/// <see cref="ListAllAsync"/> reflects a minted tag and <see cref="GetByIdAsync"/> lets AuthorRecipe
/// resolve it. Household-scoped for the uniqueness check.
/// </summary>
internal sealed class MutableTagRepository : ITagRepository
{
    public List<Tag> Items { get; }

    public MutableTagRepository(IEnumerable<Tag> seed) => Items = seed.ToList();

    public Task<Tag?> FindByNameAsync(HouseholdId householdId, string name, CancellationToken ct = default) =>
        Task.FromResult(Items.FirstOrDefault(t =>
            t.HouseholdId == householdId && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)));

    public Task<Tag?> GetByIdAsync(TagId id, CancellationToken ct = default) =>
        Task.FromResult(Items.FirstOrDefault(t => t.Id == id));

    public Task<IReadOnlyDictionary<TagId, string>> ResolveNamesAsync(
        IReadOnlyList<TagId> ids, CancellationToken ct = default)
    {
        IReadOnlyDictionary<TagId, string> result = Items
            .Where(t => ids.Contains(t.Id))
            .ToDictionary(t => t.Id, t => t.Name);
        return Task.FromResult(result);
    }

    public Task AddAsync(Tag tag, CancellationToken ct = default)
    {
        Items.Add(tag);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<Tag>> ListAllAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        var query = Items.AsEnumerable();
        if (activeOnly) query = query.Where(t => !t.IsArchived);
        return Task.FromResult<IReadOnlyList<Tag>>(query.OrderBy(t => t.Name).ToList());
    }
}
