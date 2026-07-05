using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;
using RecipesDomain = Plantry.Recipes.Domain;

namespace Plantry.Tests.Web;

/// <summary>
/// Integration tests for the edit-moment diet-tag contradiction nudge (plantry-qll2.3), driven through the real
/// <c>Plantry.Web</c> pipeline via the dedicated <c>/Recipes/DietNudge</c> fragment page with the LLM seam
/// (<see cref="IDietTagContradictionChecker"/>) and the assistive-AI gate (<see cref="IAiAssistanceGateReader"/>)
/// faked. Proves the acceptance criteria at the handler layer:
/// <list type="number">
///   <item>A dairy ingredient on a dairy-free-tagged recipe surfaces the one-line notice — then dismissing it
///   ("Keep it") makes a re-check show nothing (criterion 1).</item>
///   <item>A recipe with no Diet-category tag never calls the checker (criterion 3).</item>
///   <item>Toggle off ⇒ no checker call, no notice (criterion 4).</item>
///   <item>"Remove &lt;tag&gt; tag" drops the tag the user chose — the AI never mutates it.</item>
/// </list>
/// </summary>
public sealed class RecipeDietNudgeTests
{
    private static HttpClient AuthedClient(WebApplicationFactory<Program> f)
    {
        var client = f.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, DietNudgeFactory.HouseholdAId.ToString());
        return client;
    }

    // The dedicated fragment page renders no antiforgery token of its own, so borrow one (and its paired cookie)
    // from the editor create page — the same request-verification pair validates the Dismiss/RemoveTag POSTs.
    private static async Task<string> AntiforgeryTokenAsync(HttpClient client)
    {
        var html = await (await client.GetAsync("/Recipes/New")).Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the create page.");
        return match.Groups[1].Value;
    }

    // ── Criterion 1 (appears) ────────────────────────────────────────────────

    [Fact]
    public async Task DietNudge_gate_on_with_a_contradiction_renders_the_one_line_notice()
    {
        using var factory = new DietNudgeFactory(
            gateEnabled: true, canned: [new DietTagContradiction("Parmesan", "Dairy-Free")]);
        var client = AuthedClient(factory);

        var response = await client.GetAsync($"/Recipes/DietNudge?id={DietNudgeFactory.DietRecipeId.Value}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Parmesan", html);
        Assert.Contains("Dairy-Free", html);
        Assert.Contains("Still Dairy-Free?", html);
        // Both actions are present; the "Remove tag" action targets the recipe's own diet tag id.
        Assert.Contains("Remove Dairy-Free tag", html);
        Assert.Contains("Keep it", html);
        Assert.Contains($"tagId={DietNudgeFactory.DairyFreeTagId.Value}", html);
        Assert.True(factory.Checker.WasCalled);
    }

    // ── Criterion 4 (toggle off) ─────────────────────────────────────────────

    [Fact]
    public async Task DietNudge_gate_off_renders_nothing_and_never_calls_the_checker()
    {
        using var factory = new DietNudgeFactory(
            gateEnabled: false, canned: [new DietTagContradiction("Parmesan", "Dairy-Free")]);
        var client = AuthedClient(factory);

        var response = await client.GetAsync($"/Recipes/DietNudge?id={DietNudgeFactory.DietRecipeId.Value}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("callout", html);
        Assert.False(factory.Checker.WasCalled);
    }

    // ── Criterion 3 (no Diet-category tag) ───────────────────────────────────

    [Fact]
    public async Task DietNudge_recipe_without_a_diet_tag_renders_nothing_and_never_calls_the_checker()
    {
        using var factory = new DietNudgeFactory(
            gateEnabled: true, canned: [new DietTagContradiction("Parmesan", "Dairy-Free")]);
        var client = AuthedClient(factory);

        var response = await client.GetAsync($"/Recipes/DietNudge?id={DietNudgeFactory.PlainRecipeId.Value}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("callout", html);
        Assert.False(factory.Checker.WasCalled);
    }

    // ── Criterion 1 (dismiss → nothing on a re-check) ────────────────────────

    [Fact]
    public async Task DietNudge_dismiss_records_the_set_so_a_re_check_shows_nothing()
    {
        using var factory = new DietNudgeFactory(
            gateEnabled: true, canned: [new DietTagContradiction("Parmesan", "Dairy-Free")]);
        var client = AuthedClient(factory);
        var token = await AntiforgeryTokenAsync(client);

        var dismiss = await client.PostAsync(
            $"/Recipes/DietNudge?handler=Dismiss&id={DietNudgeFactory.DietRecipeId.Value}",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));
        Assert.Equal(HttpStatusCode.OK, dismiss.StatusCode);

        // The reconciled hash was stamped on the aggregate.
        Assert.Equal(
            factory.DietRecipe.CurrentIngredientProductHash(),
            factory.DietRecipe.DietNudgeDismissedHash);

        // A subsequent check for the same (now reconciled) ingredient set shows nothing — and never re-hits the LLM.
        factory.Checker.Reset();
        var recheck = await client.GetAsync($"/Recipes/DietNudge?id={DietNudgeFactory.DietRecipeId.Value}");
        var html = await recheck.Content.ReadAsStringAsync();
        Assert.DoesNotContain("callout", html);
        Assert.False(factory.Checker.WasCalled);
    }

    // ── "Remove tag" is the user's own action (AI never mutates tags) ────────

    [Fact]
    public async Task DietNudge_remove_tag_drops_the_tag_the_user_chose()
    {
        using var factory = new DietNudgeFactory(
            gateEnabled: true, canned: [new DietTagContradiction("Parmesan", "Dairy-Free")]);
        var client = AuthedClient(factory);
        var token = await AntiforgeryTokenAsync(client);

        Assert.Contains(DietNudgeFactory.DairyFreeTagId, factory.DietRecipe.Tags.Select(rt => rt.TagId));

        var remove = await client.PostAsync(
            $"/Recipes/DietNudge?handler=RemoveTag&id={DietNudgeFactory.DietRecipeId.Value}&tagId={DietNudgeFactory.DairyFreeTagId.Value}",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));
        Assert.Equal(HttpStatusCode.OK, remove.StatusCode);

        Assert.DoesNotContain(DietNudgeFactory.DairyFreeTagId, factory.DietRecipe.Tags.Select(rt => rt.TagId));
    }

    // ── Editor → Details trigger wiring (criteria 1 & 2, end-to-end) ─────────

    // These drive the REAL editor POST → 302 → Details GET round trip, proving the glue no other test touches:
    // the pre-save ProductId-set capture ordering, the create-vs-edit guard, and the TempData → ShowDietNudgeCheck
    // hand-off that decides whether the deferred nudge placeholder renders.

    [Fact]
    public async Task Editing_a_diet_recipe_to_add_an_ingredient_renders_the_deferred_placeholder_on_details()
    {
        using var factory = new NudgeTriggerFactory();
        var client = TriggerClient(factory);
        var token = await EditTokenAsync(client, factory.DietRecipeId.Value);

        var fields = BaseEditFields(token);
        // Add a third ingredient (Cream) — the distinct ProductId set changes (criterion 1 trigger).
        fields.Add(new("Input.Lines[2].Ordinal", "2"));
        fields.Add(new("Input.Lines[2].ProductId", NudgeTriggerFactory.CreamId.ToString()));
        fields.Add(new("Input.Lines[2].Quantity", "100"));
        fields.Add(new("Input.Lines[2].UnitId", NudgeTriggerFactory.GramUnitId.ToString()));

        var post = await client.PostAsync(
            $"/Recipes/{factory.DietRecipeId.Value}/Edit", new FormUrlEncodedContent(fields));
        Assert.True(post.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after save, got {(int)post.StatusCode}.");

        var details = await client.GetAsync(post.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        var html = await details.Content.ReadAsStringAsync();
        Assert.Contains("hx-get=\"/Recipes/DietNudge", html); // deferred placeholder present
    }

    [Fact]
    public async Task An_ingredient_neutral_edit_renders_no_placeholder_on_details()
    {
        using var factory = new NudgeTriggerFactory();
        var client = TriggerClient(factory);
        var token = await EditTokenAsync(client, factory.DietRecipeId.Value);

        var fields = BaseEditFields(token);
        // Change only the name — the ProductId set is identical, so nothing should fire (criterion 2).
        fields.RemoveAll(f => f.Key == "Input.Name");
        fields.Add(new("Input.Name", "Alfredo (renamed)"));

        var post = await client.PostAsync(
            $"/Recipes/{factory.DietRecipeId.Value}/Edit", new FormUrlEncodedContent(fields));
        Assert.True(post.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after save, got {(int)post.StatusCode}.");

        var details = await client.GetAsync(post.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        var html = await details.Content.ReadAsStringAsync();
        Assert.DoesNotContain("hx-get=\"/Recipes/DietNudge", html); // no placeholder
    }

    private static HttpClient TriggerClient(NudgeTriggerFactory factory)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, NudgeTriggerFactory.HouseholdAId.ToString());
        return client;
    }

    private static async Task<string> EditTokenAsync(HttpClient client, Guid recipeId)
    {
        var html = await (await client.GetAsync($"/Recipes/{recipeId}/Edit")).Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the edit page.");
        return match.Groups[1].Value;
    }

    // The recipe's existing two ingredients (Pasta + Garlic, units == product defaults so no conversion prompt)
    // plus its Dairy-Free tag, resubmitted so AuthorRecipe preserves them.
    private static List<KeyValuePair<string, string>> BaseEditFields(string token) =>
    [
        new("__RequestVerificationToken", token),
        new("Input.Name", "Alfredo"),
        new("Input.DefaultServings", "2"),
        new("Input.Lines[0].Ordinal", "0"),
        new("Input.Lines[0].ProductId", NudgeTriggerFactory.PastaId.ToString()),
        new("Input.Lines[0].Quantity", "400"),
        new("Input.Lines[0].UnitId", NudgeTriggerFactory.GramUnitId.ToString()),
        new("Input.Lines[1].Ordinal", "1"),
        new("Input.Lines[1].ProductId", NudgeTriggerFactory.GarlicId.ToString()),
        new("Input.Lines[1].Quantity", "3"),
        new("Input.Lines[1].UnitId", NudgeTriggerFactory.EachUnitId.ToString()),
        new("Input.TagIds[0]", NudgeTriggerFactory.DairyFreeTagId.Value.ToString()),
    ];
}

// ── WAF + fakes ──────────────────────────────────────────────────────────────────

/// <summary>
/// WAF for the diet-tag nudge fragment tests. Fakes the assistive-AI gate and the LLM checker behind the Recipes
/// ACL ports, seeds one Diet-tagged recipe (Rigatoni + Parmesan, tagged "Dairy-Free") and one untagged recipe,
/// and exposes the checker + the seeded recipe so tests can assert calls and persisted state.
/// </summary>
internal sealed class DietNudgeFactory : WebApplicationFactory<Program>
{
    public static readonly Guid HouseholdAId = RecipeEditorFixture.HouseholdAId;

    public static readonly RecipeId DietRecipeId  = RecipesDomain.RecipeId.From(Guid.Parse("d1e70000-0000-0000-0000-000000000001"));
    public static readonly RecipeId PlainRecipeId = RecipesDomain.RecipeId.From(Guid.Parse("d1e70000-0000-0000-0000-000000000002"));
    public static readonly TagId DairyFreeTagId   = new(Guid.Parse("d1e70000-0000-0000-0000-0000000000a1"));

    private static readonly Guid RigatoniId = Guid.Parse("d1e70000-0000-0000-0000-0000000000b1");
    private static readonly Guid ParmesanId = Guid.Parse("d1e70000-0000-0000-0000-0000000000b2");
    private static readonly Guid GramUnitId = Guid.Parse("d1e70000-0000-0000-0000-0000000000c1");

    private readonly bool _gateEnabled;

    public StubDietChecker Checker { get; }
    public Recipe DietRecipe { get; }
    private readonly Recipe _plainRecipe;
    private readonly MutableTagRepository _tags;

    public DietNudgeFactory(bool gateEnabled, IReadOnlyList<DietTagContradiction> canned)
    {
        _gateEnabled = gateEnabled;
        Checker = new StubDietChecker(canned);

        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
        var household = Plantry.SharedKernel.HouseholdId.From(HouseholdAId);

        var dairyFree = Tag.Create(household, "Dairy-Free", TagCategory.Diet, clock);
        SetTagId(dairyFree, DairyFreeTagId);
        _tags = new MutableTagRepository([dairyFree]);

        DietRecipe = Recipe.Create(household, "Alfredo", 2, clock).Value;
        SetRecipeId(DietRecipe, DietRecipeId);
        DietRecipe.ReplaceIngredients(
        [
            new IngredientLine(RigatoniId, 200m, GramUnitId, null, 0),
            new IngredientLine(ParmesanId, 50m, GramUnitId, null, 1),
        ], clock);
        DietRecipe.SetTags([DairyFreeTagId], clock);

        _plainRecipe = Recipe.Create(household, "Plain Pasta", 2, clock).Value;
        SetRecipeId(_plainRecipe, PlainRecipeId);
        _plainRecipe.ReplaceIngredients(
        [
            new IngredientLine(RigatoniId, 200m, GramUnitId, null, 0),
            new IngredientLine(ParmesanId, 50m, GramUnitId, null, 1),
        ], clock);
        // No tags — criterion 3.
    }

    private IReadOnlyDictionary<Guid, CatalogProductSummary> ProductSummaries() =>
        new Dictionary<Guid, CatalogProductSummary>
        {
            [RigatoniId] = new(RigatoniId, "Rigatoni", TrackStock: true),
            [ParmesanId] = new(ParmesanId, "Parmesan", TrackStock: true),
        };

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

            var tenant = new ConstantTenantContext(HouseholdAId);

            services.RemoveAll<IRecipeRepository>();
            services.AddSingleton<IRecipeRepository>(
                new FakeEditorRecipeRepository(tenant, DietRecipe, _plainRecipe));

            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(_tags);

            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeEditorProductReader(ProductSummaries(), new Dictionary<Guid, string>(), []));

            services.RemoveAll<ICatalogWriter>();
            services.AddSingleton<ICatalogWriter>(new FakeCatalogWriter());

            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeUnitConverter());

            services.RemoveAll<IAiAssistanceGateReader>();
            services.AddSingleton<IAiAssistanceGateReader>(new StubGateReader(_gateEnabled));

            // Not under test here, but the editor create page (borrowed for its antiforgery token) constructs the
            // tag suggester — stub it so the token fetch resolves regardless of the host's AI key configuration.
            services.RemoveAll<IRecipeTagSuggester>();
            services.AddSingleton<IRecipeTagSuggester>(new StubTagSuggester([]));

            // The plantry-qll2.3 seam under test: the untrusted LLM contradiction checker.
            services.RemoveAll<IDietTagContradictionChecker>();
            services.AddSingleton<IDietTagContradictionChecker>(Checker);
        });
    }

    private static void SetRecipeId(Recipe recipe, RecipeId id)
    {
        var prop = typeof(Recipe).BaseType?.BaseType?
            .GetProperty("Id", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        prop?.SetValue(recipe, id);
    }

    private static void SetTagId(Tag tag, TagId id)
    {
        var prop = typeof(Tag).BaseType?.BaseType?
            .GetProperty("Id", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        prop?.SetValue(tag, id);
    }
}

/// <summary>Fake <see cref="IDietTagContradictionChecker"/> returning canned contradictions and recording whether it ran.</summary>
internal sealed class StubDietChecker(IReadOnlyList<DietTagContradiction> canned) : IDietTagContradictionChecker
{
    public bool WasCalled { get; private set; }

    public void Reset() => WasCalled = false;

    public Task<IReadOnlyList<DietTagContradiction>> CheckAsync(
        IReadOnlyList<string> ingredientNames,
        IReadOnlyList<string> dietTagNames,
        CancellationToken ct = default)
    {
        WasCalled = true;
        return Task.FromResult(canned);
    }
}

/// <summary>
/// WAF for the editor→Details trigger round trip. Subclasses <see cref="RecipeDetailFragmentFactory"/> to inherit
/// the (heavy) Details render graph — stock/price/converter/shopping fakes — and layers on the editor + nudge
/// seams plus a Diet-tagged, editable recipe, so a single host serves BOTH the real editor POST and the real
/// Details GET. Reuses the shared catalog product ids from <c>RecipeDetailFixture</c> so the inherited Details
/// fakes already know them.
/// </summary>
internal sealed class NudgeTriggerFactory : RecipeDetailFragmentFactory
{
    public static readonly Guid HouseholdAId = RecipeDetailFixture.HouseholdAId;
    public static readonly Guid PastaId = RecipeDetailFixture.PastaId;
    public static readonly Guid GarlicId = RecipeDetailFixture.GarlicId;
    public static readonly Guid GramUnitId = RecipeDetailFixture.GramUnitId;
    public static readonly Guid EachUnitId = RecipeDetailFixture.EachUnitId;
    public static readonly Guid CreamId = Guid.Parse("d1e70000-0000-0000-0000-0000000000c9");
    public static readonly TagId DairyFreeTagId = new(Guid.Parse("d1e70000-0000-0000-0000-0000000000a2"));

    public RecipeId DietRecipeId { get; } =
        RecipesDomain.RecipeId.From(Guid.Parse("d1e70000-0000-0000-0000-000000000009"));

    private readonly Recipe _dietRecipe;
    private readonly Tag _dairyFree;

    public NudgeTriggerFactory()
    {
        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
        var household = Plantry.SharedKernel.HouseholdId.From(HouseholdAId);

        _dairyFree = Tag.Create(household, "Dairy-Free", TagCategory.Diet, clock);
        SetTagId(_dairyFree, DairyFreeTagId);

        _dietRecipe = Recipe.Create(household, "Alfredo", 2, clock).Value;
        SetRecipeId(_dietRecipe, DietRecipeId);
        _dietRecipe.ReplaceIngredients(
        [
            new IngredientLine(PastaId, 400m, GramUnitId, null, 0),
            new IngredientLine(GarlicId, 3m, EachUnitId, null, 1),
        ], clock);
        _dietRecipe.SetTags([DairyFreeTagId], clock);
    }

    private IReadOnlyDictionary<Guid, CatalogProductSummary> Summaries() =>
        new Dictionary<Guid, CatalogProductSummary>
        {
            [PastaId]  = new(PastaId, "Rigatoni", TrackStock: true),
            [GarlicId] = new(GarlicId, "Garlic Cloves", TrackStock: true),
            [CreamId]  = new(CreamId, "Heavy Cream", TrackStock: true),
        };

    private IReadOnlyDictionary<Guid, string> UnitCodes() =>
        new Dictionary<Guid, string> { [GramUnitId] = "g", [EachUnitId] = "ea" };

    private IReadOnlyList<CatalogUnitOption> UnitOptions() =>
        [new(GramUnitId, "g", "mass", 1m), new(EachUnitId, "ea", "count", 1m)];

    private IReadOnlyDictionary<Guid, Guid> DefaultUnits() =>
        new Dictionary<Guid, Guid> { [PastaId] = GramUnitId, [GarlicId] = EachUnitId, [CreamId] = GramUnitId };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder); // inherit the Details render graph fakes

        builder.ConfigureTestServices(services =>
        {
            var tenant = new ConstantTenantContext(HouseholdAId);

            // Serve the editable Diet-tagged recipe from a single mutable repo shared by editor POST + Details GET.
            services.RemoveAll<IRecipeRepository>();
            services.AddSingleton<IRecipeRepository>(new FakeEditorRecipeRepository(tenant, _dietRecipe));

            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(new MutableTagRepository([_dairyFree]));

            // Full catalog reader covering both the editor (FindAsync/ListUnits/…) and Details (ResolveSummaries).
            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeEditorProductReader(Summaries(), UnitCodes(), UnitOptions(), DefaultUnits()));

            services.RemoveAll<ICatalogWriter>();
            services.AddSingleton<ICatalogWriter>(new FakeCatalogWriter());

            services.RemoveAll<IAiAssistanceGateReader>();
            services.AddSingleton<IAiAssistanceGateReader>(new StubGateReader(true));

            services.RemoveAll<IRecipeTagSuggester>();
            services.AddSingleton<IRecipeTagSuggester>(new StubTagSuggester([]));

            services.RemoveAll<IDietTagContradictionChecker>();
            services.AddSingleton<IDietTagContradictionChecker>(
                new StubDietChecker([new DietTagContradiction("Heavy Cream", "Dairy-Free")]));
        });
    }

    private static void SetRecipeId(Recipe recipe, RecipeId id)
    {
        var prop = typeof(Recipe).BaseType?.BaseType?
            .GetProperty("Id", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        prop?.SetValue(recipe, id);
    }

    private static void SetTagId(Tag tag, TagId id)
    {
        var prop = typeof(Tag).BaseType?.BaseType?
            .GetProperty("Id", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        prop?.SetValue(tag, id);
    }
}
