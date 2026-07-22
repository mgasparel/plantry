using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
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
using Plantry.Shopping.Domain;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// Page-handler tests for the recipe-composition editor + Details UI (plantry-fqb0.3): the "Include a
/// recipe" search handler, saving inclusions (R3′ inclusions-only + interleaved ordinals via ReplaceLines),
/// cycle rejection surfacing as a validation banner (N4, no 500), the editor rendering existing inclusion
/// rows, the Details inclusion display + expandable expanded-ingredient preview, and the N5 archive guard.
/// </summary>
public sealed class RecipeInclusionTests
{
    private static readonly Guid HouseholdGuid = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");

    private static readonly Guid ParentId  = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid SubId      = Guid.Parse("b0000000-0000-0000-0000-000000000002");
    private static readonly Guid BetaId     = Guid.Parse("b0000000-0000-0000-0000-000000000003");
    private static readonly Guid CabbageId  = Guid.Parse("b0000000-0000-0000-0000-000000000004");
    private static readonly Guid OtherId    = Guid.Parse("b0000000-0000-0000-0000-000000000005");

    private static readonly Guid CashewProductId = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid GramUnitId      = Guid.Parse("d0000000-0000-0000-0000-000000000001");

    // ── Editor: "Include a recipe" search handler (D11) ─────────────────────────────────

    [Fact]
    public async Task SearchRecipes_excludes_self_and_filters_by_name()
    {
        using var factory = new EditorFactory(Alpha(), Beta(), Cabbage());
        var client = AuthedClient(factory);

        // Empty query, editing Alpha: returns every OTHER household recipe, self excluded.
        var allJson = await client.GetStringAsync($"/Recipes/{ParentId}/Edit?handler=SearchRecipes&q=");
        using var all = JsonDocument.Parse(allJson);
        var names = all.RootElement.GetProperty("hits").EnumerateArray()
            .Select(h => h.GetProperty("name").GetString()).ToList();
        Assert.Contains("Beta", names);
        Assert.Contains("Cabbage", names);
        Assert.DoesNotContain("Alpha", names); // self excluded (N2 courtesy)

        // Name filter narrows the set.
        var betaJson = await client.GetStringAsync($"/Recipes/{ParentId}/Edit?handler=SearchRecipes&q=bet");
        using var beta = JsonDocument.Parse(betaJson);
        var betaHits = beta.RootElement.GetProperty("hits").EnumerateArray().ToList();
        Assert.Single(betaHits);
        Assert.Equal("Beta", betaHits[0].GetProperty("name").GetString());
        Assert.Equal(2, betaHits[0].GetProperty("defaultServings").GetInt32());
    }

    // ── Editor: saving inclusions via ReplaceLines (R3′ inclusions-only) ────────────────

    [Fact]
    public async Task Post_saves_an_inclusions_only_recipe_via_ReplaceLines()
    {
        using var factory = new EditorFactory(SubRecipe());
        var client = AuthedClient(factory);
        var token = await AntiforgeryTokenAsync(client);

        var form = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.Name", "Assembly"),
            new("Input.DefaultServings", "2"),
            // No Input.Lines — inclusions-only (R3′).
            new("Input.Inclusions[0].Ordinal", "0"),
            new("Input.Inclusions[0].SubRecipeId", SubId.ToString()),
            new("Input.Inclusions[0].Servings", "3"),
            new("Input.Inclusions[0].SubDefaultServings", "4"),
        };

        var response = await client.PostAsync("/Recipes/New", new FormUrlEncodedContent(form));

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after save, got {(int)response.StatusCode}.");

        var saved = factory.Repo.LastAdded;
        Assert.NotNull(saved);
        Assert.Empty(saved!.Ingredients);
        var inc = Assert.Single(saved.Inclusions);
        Assert.Equal(SubId, inc.SubRecipeId.Value);
        Assert.Equal(3m, inc.Servings);
    }

    // ── Editor: cycle rejection surfaces as a validation banner (N4, no 500) ────────────

    [Fact]
    public async Task Post_that_would_create_a_cycle_re_renders_with_a_validation_message()
    {
        // Alpha edited to include Beta, but Beta already includes Alpha (edge Beta→Alpha) → cycle.
        using var factory = new EditorFactory(Alpha(), Beta());
        var client = AuthedClient(factory); // builds the host → populates factory.Repo
        factory.Repo.Edges.Add(new RecipeInclusionEdge(RecipeId.From(BetaId), RecipeId.From(ParentId)));
        var token = await AntiforgeryTokenAsync(client);

        var form = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.Name", "Alpha"),
            new("Input.DefaultServings", "2"),
            new("Input.Inclusions[0].Ordinal", "0"),
            new("Input.Inclusions[0].SubRecipeId", BetaId.ToString()),
            new("Input.Inclusions[0].Servings", "1"),
            new("Input.Inclusions[0].SubDefaultServings", "2"),
        };

        var response = await client.PostAsync($"/Recipes/{ParentId}/Edit", new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // re-render, not a 500
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("cycle", html, StringComparison.OrdinalIgnoreCase);
        Assert.Null(factory.Repo.LastSaved); // nothing persisted
    }

    // ── Editor: existing inclusion rows render into the Alpine rows[] initializer ────────

    [Fact]
    public async Task Editor_get_renders_existing_inclusion_rows()
    {
        using var factory = new EditorFactory(ParentWithInclusion(), SubRecipe());
        var client = AuthedClient(factory);

        var html = await client.GetStringAsync($"/Recipes/{ParentId}/Edit");

        // The x-data rows[] initializer is inside an HTML attribute, so its JSON quotes are entity-encoded.
        Assert.Contains("inclusion", html);        // kind:"inclusion" for the pre-populated inclusion row
        Assert.Contains("Cashew Cheese", html);    // sub name pre-populated for the row title
        Assert.Contains("Include a recipe", html); // the affordance button
    }

    // ── Editor: D13 fixed-mode servings warning carries the includer count + names ────────

    [Fact]
    public async Task Editor_get_of_an_included_recipe_emits_the_D13_warning_data()
    {
        // The sub is included by another recipe ("Nacho Platter") → editing the sub in fixed (Keep) mode
        // must have the data to warn, naming/counting its includers (D13).
        using var factory = new EditorFactory(SubRecipe(), Bare(OtherId, "Nacho Platter", 4));
        var client = AuthedClient(factory); // builds the host → populates factory.Repo
        factory.Repo.Includers[RecipeId.From(SubId)] = new HashSet<RecipeId> { RecipeId.From(OtherId) };

        var html = await client.GetStringAsync($"/Recipes/{SubId}/Edit");

        // The Alpine x-data initializer carries the includer count and name for the warning card.
        Assert.Contains("includedByCount: 1", html);
        Assert.Contains("Nacho Platter", html);
    }

    // ── Details: inclusion roll-up row + expandable full-featured child rows (D15, plantry-4037) ────────

    [Fact]
    public async Task Details_renders_inclusion_line_with_expanded_preview()
    {
        using var factory = new DetailsFactory(ParentWithInclusion(), SubRecipe());
        var client = AuthedClient(factory);

        var html = await client.GetStringAsync($"/Recipes/{ParentId}");

        // Inclusion row: "2 servings" in the amount slot, "Cashew Cheese" the row's name, linking to the sub (D15).
        Assert.Contains("2 servings", html);
        Assert.Contains("Cashew Cheese", html);
        Assert.Contains($"/Recipes/{SubId}", html);
        // Batch-fraction hint rendered (2 servings of a 4-serving sub = ½ batch, D2). The ½ glyph is
        // entity-encoded by the Razor HTML encoder, so assert on the stable "batch" token.
        Assert.Contains("batch", html);
        // Expanded child row: the sub's ingredient at factor 2/4 = 0.5 → 200g × 0.5 = 100 g. The child row
        // reuses the SAME _IngredientRow partial a direct ingredient uses (plantry-4037), which renders the
        // quantity and unit code in separate inline elements — strip tags before matching "100 g" as text.
        Assert.Contains("Cashews", html);
        var textOnly = System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " "), @"\s+", " ");
        Assert.Contains("100 g", textOnly);
    }

    // ── Details: N5 archive guard (D12) ─────────────────────────────────────────────────

    [Fact]
    public async Task Archive_blocked_when_included_by_others_surfaces_a_validation_message()
    {
        using var factory = new DetailsFactory(ParentWithInclusion(), SubRecipe());
        var client = AuthedClient(factory); // builds the host → populates factory.Repo
        // Some other recipe includes the parent → N5 blocks its archival.
        factory.Repo.Includers[RecipeId.From(ParentId)] = new HashSet<RecipeId> { RecipeId.From(OtherId) };
        var token = await AntiforgeryTokenAsync(client, $"/Recipes/{ParentId}");

        var response = await client.PostAsync(
            $"/Recipes/{ParentId}?handler=Archive",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // re-render with the error, not a 500
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("used by 1 recipe", html, StringComparison.OrdinalIgnoreCase);
        Assert.Null(factory.Repo.LastSaved); // not archived
    }

    [Fact]
    public async Task Archive_succeeds_and_redirects_when_not_included()
    {
        using var factory = new DetailsFactory(ParentWithInclusion(), SubRecipe());
        var client = AuthedClient(factory);
        var token = await AntiforgeryTokenAsync(client, $"/Recipes/{ParentId}");

        var response = await client.PostAsync(
            $"/Recipes/{ParentId}?handler=Archive",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after archive, got {(int)response.StatusCode}.");
        Assert.Equal("/Recipes", response.Headers.Location!.ToString());
        Assert.NotNull(factory.Repo.LastSaved); // archived + saved
    }

    // ── Fixture recipes ─────────────────────────────────────────────────────────────────

    private static Recipe Alpha()   => Bare(ParentId,  "Alpha",   2);
    private static Recipe Beta()    => Bare(BetaId,    "Beta",    2);
    private static Recipe Cabbage() => Bare(CabbageId, "Cabbage", 3);

    /// <summary>A bare recipe with one dummy ingredient (satisfies R3′) and a fixed id.</summary>
    private static Recipe Bare(Guid id, string name, int servings)
    {
        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
        var recipe = Recipe.Create(HouseholdId.From(HouseholdGuid), name, servings, clock).Value;
        SetId(recipe, RecipeId.From(id));
        recipe.ReplaceIngredients([new IngredientLine(CashewProductId, 100m, GramUnitId, null, 0)], clock);
        return recipe;
    }

    /// <summary>The sub-recipe: 4 servings, one tracked ingredient (200 g Cashews).</summary>
    private static Recipe SubRecipe()
    {
        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
        var recipe = Recipe.Create(HouseholdId.From(HouseholdGuid), "Cashew Cheese", 4, clock).Value;
        SetId(recipe, RecipeId.From(SubId));
        recipe.ReplaceIngredients([new IngredientLine(CashewProductId, 200m, GramUnitId, null, 0)], clock);
        return recipe;
    }

    /// <summary>The parent recipe: inclusions-only, 2 servings of the sub (factor 2/4 = ½).</summary>
    private static Recipe ParentWithInclusion()
    {
        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
        var recipe = Recipe.Create(HouseholdId.From(HouseholdGuid), "Nacho Plate", 2, clock).Value;
        SetId(recipe, RecipeId.From(ParentId));
        recipe.ReplaceLines(RecipeLineSet.Create([], [new InclusionLine(RecipeId.From(SubId), 2m, null, 0)], recipe.Id).Value, clock);
        return recipe;
    }

    private static void SetId(Recipe recipe, RecipeId id)
    {
        var prop = typeof(Recipe).BaseType?.BaseType? // Entity<RecipeId>
            .GetProperty("Id", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        prop?.SetValue(recipe, id);
    }

    // ── Test infrastructure ─────────────────────────────────────────────────────────────

    private static HttpClient AuthedClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdGuid.ToString());
        return client;
    }

    private static async Task<string> AntiforgeryTokenAsync(HttpClient client, string path = "/Recipes/New")
    {
        var html = await (await client.GetAsync(path)).Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, $"No antiforgery token found on {path}.");
        return match.Groups[1].Value;
    }

    /// <summary>
    /// Multi-recipe in-memory <see cref="IRecipeRepository"/> — returns any seeded recipe for the owning
    /// household, captures Add/Save, and exposes settable inclusion edges + includer sets for the N4/N5 paths.
    /// </summary>
    private sealed class MultiRecipeRepo(ITenantContext tenant, params Recipe[] seed) : IRecipeRepository
    {
        private readonly Dictionary<RecipeId, Recipe> _store = seed.ToDictionary(r => r.Id);
        public List<RecipeInclusionEdge> Edges { get; } = [];
        public Dictionary<RecipeId, HashSet<RecipeId>> Includers { get; } = [];
        public Recipe? LastAdded { get; private set; }
        public Recipe? LastSaved { get; private set; }

        private bool InHousehold(Recipe r) => tenant.HouseholdId is { } hid && r.HouseholdId.Value == hid;

        public Task AddAsync(Recipe r, CancellationToken ct = default)
        {
            LastAdded = r;
            _store[r.Id] = r;
            return Task.CompletedTask;
        }

        public Task<Recipe?> GetByIdAsync(RecipeId id, CancellationToken ct = default) =>
            Task.FromResult(_store.TryGetValue(id, out var r) && InHousehold(r) ? r : null);

        public Task SaveChangesAsync(CancellationToken ct = default)
        {
            LastSaved = LastAdded ?? _store.Values.FirstOrDefault();
            return Task.CompletedTask;
        }

        public Task<bool> NameExistsAsync(HouseholdId householdId, string name, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<Recipe>> ListForBrowseAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Recipe>>(
                _store.Values.Where(r => InHousehold(r) && r.ArchivedAt is null).ToList());

        public Task<IReadOnlySet<RecipeId>> ListRecipeIdsWithPhotoAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<RecipeId>>(new HashSet<RecipeId>());

        public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
            Task.FromResult(_store.Count > 0);

        public Task<IReadOnlyDictionary<RecipeId, string>> GetRecipeNamesByIdAsync(
            IReadOnlyList<RecipeId> ids, CancellationToken ct = default)
        {
            IReadOnlyDictionary<RecipeId, string> result = ids
                .Where(_store.ContainsKey)
                .Distinct()
                .ToDictionary(id => id, id => _store[id].Name);
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<RecipeInclusionEdge>> ListInclusionEdgesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RecipeInclusionEdge>>(Edges);

        public Task<IReadOnlySet<RecipeId>> GetIncluderIdsAsync(
            RecipeId subRecipeId, bool transitive = false, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<RecipeId>>(
                Includers.GetValueOrDefault(subRecipeId) ?? new HashSet<RecipeId>());
    }

    /// <summary>WAF for the editor handlers (search / save / cycle / GET render).</summary>
    private sealed class EditorFactory(params Recipe[] seed) : WebApplicationFactory<Program>
    {
        private readonly Recipe[] _seed = seed;
        public MultiRecipeRepo Repo { get; private set; } = null!;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.AddFakeDisplayCurrency();
                services.AddFakeExpiringSoonHorizon();
                AddTestAuth(services);

                Repo = new MultiRecipeRepo(
                    new ConstantTenantContext(HouseholdGuid), _seed);
                services.RemoveAll<IRecipeRepository>();
                services.AddSingleton<IRecipeRepository>(Repo);

                services.RemoveAll<ITagRepository>();
                services.AddSingleton<ITagRepository>(new FakeTagRepository(new Dictionary<TagId, string>()));

                services.RemoveAll<ICatalogProductReader>();
                services.AddSingleton<ICatalogProductReader>(new FakeEditorProductReader(
                    new Dictionary<Guid, CatalogProductSummary>(),
                    new Dictionary<Guid, string>(),
                    []));

                services.RemoveAll<ICatalogWriter>();
                services.AddSingleton<ICatalogWriter>(new FakeCatalogWriter());
            });
        }
    }

    /// <summary>WAF for the Details page (inclusion render + preview + archive).</summary>
    private sealed class DetailsFactory(params Recipe[] seed) : WebApplicationFactory<Program>
    {
        private readonly Recipe[] _seed = seed;
        public MultiRecipeRepo Repo { get; private set; } = null!;

        private static readonly IReadOnlyDictionary<Guid, CatalogProduct> Products =
            new Dictionary<Guid, CatalogProduct>
            {
                [CashewProductId] = new(CashewProductId, "Cashews", TrackStock: true,
                    DefaultUnitId: GramUnitId, ParentProductId: null, IsParent: false, VariantProductIds: []),
            };

        private static readonly IReadOnlyDictionary<Guid, string> UnitCodes =
            new Dictionary<Guid, string> { [GramUnitId] = "g" };

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.AddFakeDisplayCurrency();
                services.AddFakeExpiringSoonHorizon();
                AddTestAuth(services);

                Repo = new MultiRecipeRepo(new ConstantTenantContext(HouseholdGuid), _seed);
                services.RemoveAll<IRecipeRepository>();
                services.AddScoped<IRecipeRepository>(_ => Repo);

                services.RemoveAll<ITagRepository>();
                services.AddSingleton<ITagRepository>(new FakeTagRepository(new Dictionary<TagId, string>()));

                services.RemoveAll<ICatalogProductReader>();
                services.AddSingleton<ICatalogProductReader>(new FakeCatalogProductReader(Products, UnitCodes));

                services.RemoveAll<IInventoryStockReader>();
                services.AddSingleton<IInventoryStockReader>(
                    new FakeDetailStockReader(new Dictionary<Guid, ProductStock>()));

                services.RemoveAll<IPriceReader>();
                services.AddSingleton<IPriceReader>(new FakeDetailPriceReader(new Dictionary<Guid, PricePoint>()));

                services.RemoveAll<IUnitConverter>();
                services.AddSingleton<IUnitConverter>(new FakeDetailUnitConverter());
                services.AddFakeQuantityFormatter();

                services.RemoveAll<IShoppingListWriter>();
                services.AddSingleton<IShoppingListWriter>(new NullInclusionShoppingWriter());

                services.RemoveAll<IShoppingListRepository>();
                services.AddScoped<IShoppingListRepository, NullInclusionShoppingRepo>();
            });
        }
    }

    private static void AddTestAuth(IServiceCollection services) =>
        services.AddAuthentication(opts =>
            {
                opts.DefaultScheme = TestAuthHandler.SchemeName;
                opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

    private sealed class NullInclusionShoppingWriter : IShoppingListWriter
    {
        public Task<ShoppingSyncOutcome> SyncSourceContributionAsync(
            IReadOnlyList<ShoppingItem> items, string source, Guid sourceRef, CancellationToken ct = default) =>
            Task.FromResult(ShoppingSyncOutcome.None);
    }

    private sealed class NullInclusionShoppingRepo : IShoppingListRepository
    {
        public Task<ShoppingList?> GetForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
            Task.FromResult<ShoppingList?>(null);
        public Task<ShoppingList?> GetByIdAsync(ShoppingListId id, CancellationToken ct = default) =>
            Task.FromResult<ShoppingList?>(null);
        public Task AddAsync(ShoppingList list, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
