using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Catalog.Domain;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Shopping.Domain;
using Plantry.Tests.Web.Infrastructure;
using Plantry.Web.Recipes;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Web;

/// <summary>
/// Proves the Details inclusion sub-recipe preview renders amounts through <see cref="IQuantityFormatter"/>
/// the same way the primary ingredient list does (plantry-51c3) — vulgar fractions, not the raw
/// <c>0.###</c> decimal — closing the visible inconsistency where a preview showed "0.5 cup" while the
/// primary row for the same amount showed "½ cup". The preview is a distinct render path built in
/// <c>DetailsModel.BuildInclusionViewsAsync</c> from expanded lines (out of scope for plantry-vci8.3).
///
/// Fixture: an inclusions-only parent (2 servings of a 4-serving sub → expansion factor ½) whose sub has a
/// Fraction-styled 1 cup line (→ 0.5 cup = "½"), a Decimal-styled 100 g line (→ 50 g, the decimal path), and
/// an untracked 7 g line (→ quantity hidden). A recording formatter over those units also captures the call
/// count + Simplify flag so the batching contract (one page-wide call, Simplify:false at 1×) is pinned.
/// </summary>
public sealed class InclusionPreviewQuantityDisplayTests
{
    private static readonly Guid HouseholdGuid = Guid.Parse("e1e1e1e1-0000-0000-0000-000000000001");
    private static readonly Guid ParentId = Guid.Parse("e0000000-0000-0000-0000-000000000001");
    private static readonly Guid SubId = Guid.Parse("e0000000-0000-0000-0000-000000000002");

    private static readonly Guid ButterId = Guid.Parse("f0000000-0000-0000-0000-000000000001");
    private static readonly Guid FlourId = Guid.Parse("f0000000-0000-0000-0000-000000000002");
    private static readonly Guid SaltId = Guid.Parse("f0000000-0000-0000-0000-000000000003");

    // cup: Fraction-styled US-customary volume. g: Decimal-styled mass. (Factors are irrelevant here — at 1×
    // no simplification runs, so only each unit's DisplayStyle drives the render.)
    private static readonly CatalogUnit Cup = MakeUnit("cup", Dimension.Volume, 240m, DisplayStyle.Fraction);
    private static readonly CatalogUnit Gram = MakeUnit("g", Dimension.Mass, 1m, DisplayStyle.Decimal);
    private static readonly IReadOnlyList<CatalogUnit> Units = [Cup, Gram];

    // ── Preview renders the authored amount as a vulgar fraction, matching the primary row (Q1) ──────────
    [Fact]
    public async Task Preview_renders_authored_half_cup_as_a_fraction()
    {
        using var factory = new PreviewFactory();
        var client = AuthedClient(factory);

        var raw = await client.GetStringAsync($"/Recipes/{ParentId}");
        var html = System.Net.WebUtility.HtmlDecode(raw);

        // Sub's 1 cup × (2/4) = 0.5 cup → "½ cup" in the preview, not "0.5 cup".
        Assert.Contains("½ cup", html);
        Assert.DoesNotContain("0.5 cup", html);
    }

    // ── A Decimal-styled unit falls back to the canonical decimal, exactly as the primary rows do ────────
    [Fact]
    public async Task Preview_renders_decimal_styled_unit_as_the_plain_decimal()
    {
        using var factory = new PreviewFactory();
        var client = AuthedClient(factory);

        var raw = await client.GetStringAsync($"/Recipes/{ParentId}");
        var html = System.Net.WebUtility.HtmlDecode(raw);

        // Sub's 100 g × (2/4) = 50 g → the plain "50 g" (g is Decimal-styled — no fraction glyph).
        Assert.Contains("50 g", html);
    }

    // ── An untracked staple still hides its quantity entirely (existing behavior preserved) ──────────────
    [Fact]
    public async Task Preview_hides_the_quantity_for_an_untracked_product()
    {
        using var factory = new PreviewFactory();
        var client = AuthedClient(factory);

        var raw = await client.GetStringAsync($"/Recipes/{ParentId}");
        var html = System.Net.WebUtility.HtmlDecode(raw);

        // Salt renders as a name-only preview line; its 7 g × (2/4) = 3.5 g amount is never shown.
        Assert.Contains("Salt", html);
        Assert.DoesNotContain("3.5 g", html);
    }

    // ── The formatter is hit exactly once for the whole page, and always with Simplify:false at 1× ───────
    [Fact]
    public async Task Preview_batches_one_simplify_false_formatter_call_for_the_whole_page()
    {
        using var factory = new PreviewFactory();
        var client = AuthedClient(factory);

        await client.GetStringAsync($"/Recipes/{ParentId}");

        // One page-wide call (parent is inclusions-only, so no direct-ingredient call precedes it) carrying
        // both tracked preview lines (Butter cup + Flour g); the untracked Salt line is excluded.
        var call = Assert.Single(factory.Formatter.Calls);
        Assert.Equal(2, call.Count);
        Assert.All(call, r => Assert.False(r.Simplify));
    }

    // ── Fixture ──────────────────────────────────────────────────────────────────────────────────────────

    private static CatalogUnit MakeUnit(string code, Dimension dimension, decimal factorToBase, DisplayStyle style)
    {
        var unit = CatalogUnit.Create(
            HouseholdId.From(HouseholdGuid), code, code, dimension, factorToBase, isBase: false, UnitSystem.UsCustomary);
        unit.SetDisplayStyle(style);
        return unit;
    }

    private static Recipe SubRecipe()
    {
        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
        var recipe = Recipe.Create(HouseholdId.From(HouseholdGuid), "Cashew Cheese", defaultServings: 4, clock).Value;
        SetId(recipe, RecipeId.From(SubId));
        recipe.ReplaceIngredients(
        [
            new IngredientLine(ButterId, 1m, Cup.Id.Value, GroupHeading: null, Ordinal: 0),
            new IngredientLine(FlourId, 100m, Gram.Id.Value, GroupHeading: null, Ordinal: 1),
            new IngredientLine(SaltId, 7m, Gram.Id.Value, GroupHeading: null, Ordinal: 2),
        ], clock);
        return recipe;
    }

    private static Recipe ParentWithInclusion()
    {
        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
        var recipe = Recipe.Create(HouseholdId.From(HouseholdGuid), "Nacho Plate", defaultServings: 2, clock).Value;
        SetId(recipe, RecipeId.From(ParentId));
        recipe.ReplaceLines([], [new InclusionLine(RecipeId.From(SubId), 2m, null, 0)], clock);
        return recipe;
    }

    private static void SetId(Recipe recipe, RecipeId id)
    {
        var prop = typeof(Recipe).BaseType?.BaseType? // Entity<RecipeId>
            .GetProperty("Id", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        prop?.SetValue(recipe, id);
    }

    private static HttpClient AuthedClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdGuid.ToString());
        return client;
    }

    /// <summary>Records every <see cref="FormatAsync"/> call while formatting through the identical live logic.</summary>
    private sealed class RecordingQuantityFormatter(IReadOnlyList<CatalogUnit> units) : IQuantityFormatter
    {
        public List<IReadOnlyList<QuantityFormatRequest>> Calls { get; } = [];

        public Task<IReadOnlyDictionary<string, FormattedQuantity>> FormatAsync(
            IReadOnlyList<QuantityFormatRequest> requests, CancellationToken ct = default)
        {
            Calls.Add(requests);
            return Task.FromResult(QuantityFormatting.Format(requests, units));
        }
    }

    /// <summary>Minimal multi-recipe repository so the real expansion service can resolve the sub.</summary>
    private sealed class TwoRecipeRepo(ITenantContext tenant, params Recipe[] seed) : IRecipeRepository
    {
        private readonly Dictionary<RecipeId, Recipe> _store = seed.ToDictionary(r => r.Id);

        private bool InHousehold(Recipe r) => tenant.HouseholdId is { } hid && r.HouseholdId.Value == hid;

        public Task<Recipe?> GetByIdAsync(RecipeId id, CancellationToken ct = default) =>
            Task.FromResult(_store.TryGetValue(id, out var r) && InHousehold(r) ? r : null);

        public Task AddAsync(Recipe r, CancellationToken ct = default) { _store[r.Id] = r; return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> NameExistsAsync(HouseholdId householdId, string name, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<IReadOnlyList<Recipe>> ListForBrowseAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Recipe>>(_store.Values.Where(InHousehold).ToList());
        public Task<IReadOnlySet<RecipeId>> ListRecipeIdsWithPhotoAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<RecipeId>>(new HashSet<RecipeId>());
        public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
            Task.FromResult(_store.Count > 0);
        public Task<IReadOnlyDictionary<RecipeId, string>> GetRecipeNamesByIdAsync(
            IReadOnlyList<RecipeId> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<RecipeId, string>>(
                ids.Where(_store.ContainsKey).Distinct().ToDictionary(id => id, id => _store[id].Name));
        public Task<IReadOnlyList<RecipeInclusionEdge>> ListInclusionEdgesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RecipeInclusionEdge>>([]);
        public Task<IReadOnlySet<RecipeId>> GetIncluderIdsAsync(
            RecipeId subRecipeId, bool transitive = false, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<RecipeId>>(new HashSet<RecipeId>());
    }

    private sealed class PreviewFactory : WebApplicationFactory<Program>
    {
        public RecordingQuantityFormatter Formatter { get; } = new(Units);

        private static readonly IReadOnlyDictionary<Guid, CatalogProduct> Products =
            new Dictionary<Guid, CatalogProduct>
            {
                [ButterId] = new(ButterId, "Butter", TrackStock: true, Cup.Id.Value, null, IsParent: false, []),
                [FlourId] = new(FlourId, "Flour", TrackStock: true, Gram.Id.Value, null, IsParent: false, []),
                [SaltId] = new(SaltId, "Salt", TrackStock: false, Gram.Id.Value, null, IsParent: false, []),
            };

        private static readonly IReadOnlyDictionary<Guid, string> UnitCodes =
            new Dictionary<Guid, string> { [Cup.Id.Value] = "cup", [Gram.Id.Value] = "g" };

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
                services.AddScoped<IRecipeRepository>(sp =>
                    new TwoRecipeRepo(sp.GetRequiredService<ITenantContext>(), ParentWithInclusion(), SubRecipe()));

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

                services.RemoveAll<IQuantityFormatter>();
                services.AddSingleton<IQuantityFormatter>(Formatter);

                services.RemoveAll<IShoppingListWriter>();
                services.AddSingleton<IShoppingListWriter>(new NullPreviewShoppingWriter());
                services.RemoveAll<IShoppingListRepository>();
                services.AddScoped<IShoppingListRepository, NullPreviewShoppingRepo>();
            });
        }
    }

    private sealed class NullPreviewShoppingWriter : IShoppingListWriter
    {
        public Task<ShoppingSyncOutcome> SyncSourceContributionAsync(
            IReadOnlyList<ShoppingItem> items, string source, Guid sourceRef, CancellationToken ct = default) =>
            Task.FromResult(ShoppingSyncOutcome.None);
    }

    private sealed class NullPreviewShoppingRepo : IShoppingListRepository
    {
        public Task<ShoppingList?> GetForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
            Task.FromResult<ShoppingList?>(null);
        public Task<ShoppingList?> GetByIdAsync(ShoppingListId id, CancellationToken ct = default) =>
            Task.FromResult<ShoppingList?>(null);
        public Task AddAsync(ShoppingList list, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
