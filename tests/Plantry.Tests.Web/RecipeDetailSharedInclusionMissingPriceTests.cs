using AngleSharp.Html.Parser;
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
/// Pins the product-based (not line-based) semantics of the Partial cost popover's bolded count
/// (plantry-rpg8): the costing engine aggregates costable lines by (ProductId, UnitId)
/// (<see cref="Plantry.Recipes.Domain.CostingService"/>), but the popover's job is actionable — click
/// each product to price it — so the truthful unit is distinct products, not lines.
///
/// <para>Fixture: Cheese is un-priced and appears as TWO separate costable lines — a direct parent line
/// in grams, and an inclusion child line (via a sub-recipe) in "each" — the exact shape that made the old
/// line-based "N of M" copy diverge from the one-row link list beneath it (2 missing LINES, 1 missing
/// PRODUCT). Flour is priced so the recipe costs to <see cref="CostCompleteness.Partial"/> rather than
/// <see cref="CostCompleteness.None"/>.</para>
/// </summary>
public sealed class RecipeDetailSharedInclusionMissingPriceTests
{
    private static readonly Guid HouseholdGuid = Guid.Parse("f2f2f2f2-0000-0000-0000-000000000001");

    private static readonly Guid ParentId = Guid.Parse("a4000000-0000-0000-0000-000000000001");
    private static readonly Guid SubId = Guid.Parse("a4000000-0000-0000-0000-000000000002");

    private static readonly Guid FlourId = Guid.Parse("a5000000-0000-0000-0000-000000000001"); // priced
    private static readonly Guid CheeseId = Guid.Parse("a5000000-0000-0000-0000-000000000002"); // un-priced, shared

    private static readonly Guid GramUnitId = Guid.Parse("a6000000-0000-0000-0000-000000000001");
    private static readonly Guid EachUnitId = Guid.Parse("a6000000-0000-0000-0000-000000000002");

    private static readonly HtmlParser Parser = new();

    [Fact]
    public async Task Partial_popover_bolds_the_distinct_product_count_not_the_line_count()
    {
        using var factory = new SharedInclusionFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdGuid.ToString());

        var html = await client.GetStringAsync($"/Recipes/{ParentId}");
        var doc = Parser.ParseDocument(html);
        var content = doc.QuerySelector(".rd-meta__flag .popover__content")
            ?? throw new InvalidOperationException("Missing-price popover content not found.");

        // Cheese is costed across TWO lines (parent, grams + inclusion child, each) — the costing engine's
        // line-based tally would read CostableCount=3, PricedCount=1 → old copy "2 of 3". Only ONE distinct
        // product (Cheese) is actually un-priced, and only ONE link row renders — the bolded count must
        // match the rendered rows, not the line-based tally, and the "of M" denominator must be gone.
        Assert.Contains("1 ingredient", content.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(" of ", content.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("2 ingredients", content.TextContent, StringComparison.Ordinal);

        var links = content.QuerySelectorAll("a").ToList();
        Assert.Single(links);
        Assert.Equal($"/Pantry/Products/Detail/{CheeseId}", links[0].GetAttribute("href"));
    }

    // ── Fixture ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Sub-recipe "Cheese Topping": Cheese, 2 "ea", un-priced.</summary>
    private static Recipe SubRecipe()
    {
        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
        var recipe = Recipe.Create(HouseholdId.From(HouseholdGuid), "Cheese Topping", defaultServings: 4, clock).Value;
        SetId(recipe, RecipeId.From(SubId));
        recipe.ReplaceIngredients(
        [
            new IngredientLine(CheeseId, 2m, EachUnitId, GroupHeading: null, Ordinal: 0),
        ], clock);
        return recipe;
    }

    /// <summary>
    /// Parent "Test Parent": Flour (priced, grams) direct, Cheese (un-priced, grams) direct, plus the
    /// Cheese Topping inclusion at 2 servings (factor 2/4 = 0.5) — Cheese's SECOND costable line, in "each".
    /// </summary>
    private static Recipe ParentRecipe()
    {
        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
        var recipe = Recipe.Create(HouseholdId.From(HouseholdGuid), "Test Parent", defaultServings: 4, clock).Value;
        SetId(recipe, RecipeId.From(ParentId));
        var lineSet = RecipeLineSet.Create(
            ingredients:
            [
                new IngredientLine(FlourId, 200m, GramUnitId, GroupHeading: "Base", Ordinal: 1),
                new IngredientLine(CheeseId, 100m, GramUnitId, GroupHeading: "Base", Ordinal: 2),
            ],
            inclusions:
            [
                new InclusionLine(RecipeId.From(SubId), 2m, "Base", 3),
            ],
            recipe.Id).Value;
        recipe.ReplaceLines(lineSet, clock);
        return recipe;
    }

    private static void SetId(Recipe recipe, RecipeId id)
    {
        var prop = typeof(Recipe).BaseType?.BaseType? // Entity<RecipeId>
            .GetProperty("Id", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        prop?.SetValue(recipe, id);
    }

    /// <summary>Multi-recipe in-memory repository so the real expansion service can resolve the sub.</summary>
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

    private sealed class SharedInclusionFactory : WebApplicationFactory<Program>
    {
        private static readonly IReadOnlyDictionary<Guid, CatalogProduct> Products =
            new Dictionary<Guid, CatalogProduct>
            {
                [FlourId] = new(FlourId, "Flour", TrackStock: true, GramUnitId, null, IsParent: false, []),
                [CheeseId] = new(CheeseId, "Cheese", TrackStock: true, GramUnitId, null, IsParent: false, []),
            };

        private static readonly IReadOnlyDictionary<Guid, string> UnitCodes =
            new Dictionary<Guid, string> { [GramUnitId] = "g", [EachUnitId] = "ea" };

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

                services.RemoveAll<IRecipeRepository>();
                services.AddScoped<IRecipeRepository>(sp =>
                    new TwoRecipeRepo(sp.GetRequiredService<ITenantContext>(), ParentRecipe(), SubRecipe()));

                services.RemoveAll<ITagRepository>();
                services.AddSingleton<ITagRepository>(new FakeTagRepository(new Dictionary<TagId, string>()));

                services.RemoveAll<ICatalogProductReader>();
                services.AddSingleton<ICatalogProductReader>(new FakeCatalogProductReader(Products, UnitCodes));

                services.RemoveAll<IInventoryStockReader>();
                services.AddSingleton<IInventoryStockReader>(
                    new FakeDetailStockReader(new Dictionary<Guid, ProductStock>()));

                services.RemoveAll<IPriceReader>();
                services.AddSingleton<IPriceReader>(new FakeDetailPriceReader(new Dictionary<Guid, PricePoint>
                {
                    [FlourId] = new(FlourId, Price: 2.00m, Quantity: 1000m, UnitId: GramUnitId, UnitPrice: 0.002m),
                    // CheeseId intentionally omitted on BOTH lines → Partial cost, product-based popover.
                }));

                services.RemoveAll<IUnitConverter>();
                services.AddSingleton<IUnitConverter>(new FakeDetailUnitConverter());
                services.AddFakeQuantityFormatter();

                services.RemoveAll<IShoppingListWriter>();
                services.AddSingleton<IShoppingListWriter>(new NullSharedInclusionShoppingWriter());
                services.RemoveAll<IShoppingListRepository>();
                services.AddScoped<IShoppingListRepository, NullSharedInclusionShoppingRepo>();
            });
        }
    }

    private sealed class NullSharedInclusionShoppingWriter : IShoppingListWriter
    {
        public Task<ShoppingSyncOutcome> SyncSourceContributionAsync(
            IReadOnlyList<ShoppingItem> items, string source, Guid sourceRef, CancellationToken ct = default) =>
            Task.FromResult(ShoppingSyncOutcome.None);
    }

    private sealed class NullSharedInclusionShoppingRepo : IShoppingListRepository
    {
        public Task<ShoppingList?> GetForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
            Task.FromResult<ShoppingList?>(null);
        public Task<ShoppingList?> GetByIdAsync(ShoppingListId id, CancellationToken ct = default) =>
            Task.FromResult<ShoppingList?>(null);
        public Task AddAsync(ShoppingList list, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
