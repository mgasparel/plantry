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
/// Integration tests for the reverse-ripple diet-tag nudge (recipe-composition.md D10 / plantry-fqb0.7), driven
/// through the real <c>Plantry.Web</c> pipeline. Two surfaces are exercised:
/// <list type="number">
///   <item>The dedicated <c>/Recipes/DietNudge?handler=Ripple</c> fragment — the per-parent notice rendered on a
///   saved SUB's landing, naming the including PARENT ("may conflict with 'Vegan' on Vegan Nachos"), with the
///   assistive-AI gate + LLM checker faked. Keep-it / Remove-tag target the PARENT; Remove-tag emits NO out-of-band
///   chip delete (the parent's chip is not on the sub's page).</item>
///   <item>The sub's Details landing renders one deferred placeholder per <c>?rippleParents</c> id, and none on a
///   plain view — proving the LLM check is confined to the save landing.</item>
/// </list>
/// </summary>
public sealed class RecipeDietRippleNudgeTests
{
    private static HttpClient AuthedClient(WebApplicationFactory<Program> f)
    {
        var client = f.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, RippleNudgeFactory.HouseholdAId.ToString());
        return client;
    }

    // The fragment page renders no antiforgery token of its own; borrow one (and its cookie) from the create page.
    private static async Task<string> AntiforgeryTokenAsync(HttpClient client)
    {
        var html = await (await client.GetAsync("/Recipes/New")).Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the create page.");
        return match.Groups[1].Value;
    }

    // ── Fragment renders, naming the parent (criteria: conflicts surface naming the parent + its tag) ──

    [Fact]
    public async Task Ripple_fragment_names_the_parent_and_its_tag_with_actions_targeting_the_parent()
    {
        using var factory = new RippleNudgeFactory(gateEnabled: true);
        var client = AuthedClient(factory);

        var response = await client.GetAsync(
            $"/Recipes/DietNudge?handler=Ripple&id={RippleNudgeFactory.ParentId.Value}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        // Names the parent AND its tag (recipe-composition.md D10 wording — "may conflict with 'Vegan' on <parent>").
        Assert.Contains("Vegan Nachos", html);
        Assert.Contains("may conflict with Vegan on Vegan Nachos", html);
        Assert.Contains("Parmesan", html);
        // Keep-it / Remove-tag target the PARENT (its id + its own diet tag id), not the sub.
        Assert.Contains($"handler=RippleRemoveTag&id={RippleNudgeFactory.ParentId.Value}", html);
        Assert.Contains($"tagId={RippleNudgeFactory.VeganTagId.Value}", html);
        Assert.Contains($"handler=Dismiss&id={RippleNudgeFactory.ParentId.Value}", html);
        // Callout id is parent-scoped so multiple ripple callouts never collide.
        Assert.Contains($"id=\"diet-ripple-{RippleNudgeFactory.ParentId.Value}\"", html);
        Assert.True(factory.Checker.WasCalled);
    }

    // ── Gate off ⇒ no checker call, no notice (acceptance criterion 3) ──

    [Fact]
    public async Task Ripple_fragment_gate_off_renders_nothing_and_never_calls_the_checker()
    {
        using var factory = new RippleNudgeFactory(gateEnabled: false);
        var client = AuthedClient(factory);

        var response = await client.GetAsync(
            $"/Recipes/DietNudge?handler=Ripple&id={RippleNudgeFactory.ParentId.Value}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("callout", html);
        Assert.False(factory.Checker.WasCalled);
    }

    // ── Remove-tag drops the PARENT's tag; no OOB chip delete on the sub's page ──

    [Fact]
    public async Task Ripple_remove_tag_drops_the_parents_tag_and_returns_no_oob_chip_delete()
    {
        using var factory = new RippleNudgeFactory(gateEnabled: true);
        var client = AuthedClient(factory);
        var token = await AntiforgeryTokenAsync(client);

        Assert.Contains(RippleNudgeFactory.VeganTagId, factory.Parent.Tags.Select(rt => rt.TagId));

        var remove = await client.PostAsync(
            $"/Recipes/DietNudge?handler=RippleRemoveTag&id={RippleNudgeFactory.ParentId.Value}&tagId={RippleNudgeFactory.VeganTagId.Value}",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));
        Assert.Equal(HttpStatusCode.OK, remove.StatusCode);

        // The PARENT's tag was dropped (the user's own action; the AI never mutates tags).
        Assert.DoesNotContain(RippleNudgeFactory.VeganTagId, factory.Parent.Tags.Select(rt => rt.TagId));
        // No out-of-band chip delete — the parent's hero chip is not on the sub's landing.
        var body = await remove.Content.ReadAsStringAsync();
        Assert.DoesNotContain("hx-swap-oob", body);
        Assert.Equal(string.Empty, body.Trim());
    }

    // ── Keep-it (Dismiss) reconciles the parent's expanded set so a re-check shows nothing ──

    [Fact]
    public async Task Ripple_keep_it_reconciles_the_parent_so_a_recheck_shows_nothing()
    {
        using var factory = new RippleNudgeFactory(gateEnabled: true);
        var client = AuthedClient(factory);
        var token = await AntiforgeryTokenAsync(client);

        var dismiss = await client.PostAsync(
            $"/Recipes/DietNudge?handler=Dismiss&id={RippleNudgeFactory.ParentId.Value}",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));
        Assert.Equal(HttpStatusCode.OK, dismiss.StatusCode);

        // A subsequent ripple check for the same (now reconciled) expanded set shows nothing and never re-hits the LLM.
        factory.Checker.Reset();
        var recheck = await client.GetAsync(
            $"/Recipes/DietNudge?handler=Ripple&id={RippleNudgeFactory.ParentId.Value}");
        var html = await recheck.Content.ReadAsStringAsync();
        Assert.DoesNotContain("callout", html);
        Assert.False(factory.Checker.WasCalled);
    }

    // ── The sub's Details landing renders one placeholder per ?rippleParents id (and none on a plain view) ──

    [Fact]
    public async Task Sub_landing_renders_one_ripple_placeholder_per_parent_and_none_on_a_plain_view()
    {
        using var factory = new RecipeDetailFragmentFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, RecipeDetailFixture.HouseholdAId.ToString());

        var p1 = Guid.Parse("cafe0000-0000-0000-0000-000000000001");
        var p2 = Guid.Parse("cafe0000-0000-0000-0000-000000000002");

        var landing = await (await client.GetAsync(
            $"/Recipes/{factory.RecipeId}?rippleParents={p1},{p2}")).Content.ReadAsStringAsync();
        Assert.Contains($"/Recipes/DietNudge?handler=Ripple&id={p1}", landing);
        Assert.Contains($"/Recipes/DietNudge?handler=Ripple&id={p2}", landing);

        var plain = await (await client.GetAsync($"/Recipes/{factory.RecipeId}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("handler=Ripple", plain);
    }
}

// ── WAF + fakes ──────────────────────────────────────────────────────────────────

/// <summary>
/// WAF for the reverse-ripple fragment tests. Seeds a dairy SUB ("Cheese Sauce") included by a Vegan-tagged PARENT
/// ("Vegan Nachos"); the parent's EXPANDED set carries the sub's dairy, so a ripple check contradicts. The
/// includers lookup is served by <see cref="FakeEditorRecipeRepository"/> (which now computes edges from the known
/// recipes' inclusions). Exposes the parent + checker so tests can assert persisted state and calls.
/// </summary>
internal sealed class RippleNudgeFactory : WebApplicationFactory<Program>
{
    public static readonly Guid HouseholdAId = RecipeEditorFixture.HouseholdAId;

    public static readonly RecipeId SubId    = RecipesDomain.RecipeId.From(Guid.Parse("f9b00000-0000-0000-0000-000000000001"));
    public static readonly RecipeId ParentId = RecipesDomain.RecipeId.From(Guid.Parse("f9b00000-0000-0000-0000-000000000002"));
    public static readonly TagId VeganTagId  = new(Guid.Parse("f9b00000-0000-0000-0000-0000000000a1"));

    private static readonly Guid PastaId = Guid.Parse("f9b00000-0000-0000-0000-0000000000b1");
    private static readonly Guid DairyId = Guid.Parse("f9b00000-0000-0000-0000-0000000000b2");
    private static readonly Guid GramUnitId = Guid.Parse("f9b00000-0000-0000-0000-0000000000c1");

    private readonly bool _gateEnabled;

    public StubDietChecker Checker { get; }
    public Recipe Parent { get; }
    private readonly Recipe _sub;
    private readonly MutableTagRepository _tags;

    public RippleNudgeFactory(bool gateEnabled)
    {
        _gateEnabled = gateEnabled;
        Checker = new StubDietChecker([new DietTagContradiction("Parmesan", "Vegan")]);

        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
        var household = Plantry.SharedKernel.HouseholdId.From(HouseholdAId);

        var vegan = Tag.Create(household, "Vegan", TagCategory.Diet, clock);
        SetTagId(vegan, VeganTagId);
        _tags = new MutableTagRepository([vegan]);

        _sub = Recipe.Create(household, "Cheese Sauce", 2, clock).Value;
        SetRecipeId(_sub, SubId);
        _sub.ReplaceLines(RecipeLineSet.Create([new IngredientLine(DairyId, 100m, GramUnitId, null, 0)], [], _sub.Id).Value, clock);

        Parent = Recipe.Create(household, "Vegan Nachos", 2, clock).Value;
        SetRecipeId(Parent, ParentId);
        Parent.ReplaceLines(
            RecipeLineSet.Create(
                [new IngredientLine(PastaId, 200m, GramUnitId, null, 0)],
                [new InclusionLine(SubId, 2m, null, 1)],
                Parent.Id).Value,
            clock);
        Parent.SetTags([VeganTagId], clock);
    }

    private IReadOnlyDictionary<Guid, CatalogProductSummary> ProductSummaries() =>
        new Dictionary<Guid, CatalogProductSummary>
        {
            [PastaId] = new(PastaId, "Rigatoni", TrackStock: true),
            [DairyId] = new(DairyId, "Parmesan", TrackStock: true),
        };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.AddFakeDisplayCurrency();
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
            services.AddSingleton<IRecipeRepository>(new FakeEditorRecipeRepository(tenant, Parent, _sub));

            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(_tags);

            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeEditorProductReader(ProductSummaries(), new Dictionary<Guid, string>(), []));

            services.RemoveAll<ICatalogWriter>();
            services.AddSingleton<ICatalogWriter>(new FakeCatalogWriter());

            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeUnitConverter());
            services.AddFakeQuantityFormatter();

            services.RemoveAll<IAiAssistanceGateReader>();
            services.AddSingleton<IAiAssistanceGateReader>(new StubGateReader(_gateEnabled));

            // The create page (borrowed for its antiforgery token) constructs the tag suggester — stub it.
            services.RemoveAll<IRecipeTagSuggester>();
            services.AddSingleton<IRecipeTagSuggester>(new StubTagSuggester([]));

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
