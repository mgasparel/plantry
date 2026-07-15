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

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// Fixture data + in-memory doubles for the Cook page recipe-composition tests (plantry-fqb0.9).
///
/// Scenario: parent "Nachos" (4 servings) with one direct tracked leaf ingredient (Chips) and an
/// inclusion of "Nacho Cheese" (2 servings). The sub "Nacho Cheese" (4 servings) has two tracked leaf
/// ingredients (Cashews, Nutritional Yeast). At the default 4 servings the inclusion factor is
/// 2/4 = 0.5, so the sub's lines expand to Cashews 50g and Nutritional Yeast 10g, and the group header
/// reads "Nacho Cheese — 2 servings".
/// </summary>
public static class CookInclusionFixture
{
    public static readonly Guid HouseholdAId = CookConfirmFixture.HouseholdAId;

    public static readonly Guid ChipsId    = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    public static readonly Guid CashewsId  = Guid.Parse("c0000000-0000-0000-0000-000000000002");
    public static readonly Guid YeastId    = Guid.Parse("c0000000-0000-0000-0000-000000000003");

    // A parent-product ingredient inside the sub, so the expanded inclusion line renders the Variant
    // Disambiguation Picker (C7/C11) and exercises the InclusionPickerSelections swap path.
    public static readonly Guid MilkParentId = Guid.Parse("c0000000-0000-0000-0000-000000000004");
    public static readonly Guid OatMilkId    = Guid.Parse("c0000000-0000-0000-0000-000000000005");
    public static readonly Guid SoyMilkId    = Guid.Parse("c0000000-0000-0000-0000-000000000006");

    public static readonly Guid GramUnitId = CookConfirmFixture.GramUnitId;

    /// <summary>Builds the sub-recipe (Nacho Cheese) and the parent (Nachos) that includes it.</summary>
    public static (Recipe Parent, Recipe Sub) Build()
    {
        var hid = HouseholdId.From(HouseholdAId);
        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;

        var sub = Recipe.Create(hid, "Nacho Cheese", defaultServings: 4, clock).Value;
        sub.ReplaceIngredients(
        [
            new IngredientLine(CashewsId,    100m, GramUnitId, GroupHeading: null, Ordinal: 1),
            new IngredientLine(YeastId,       20m, GramUnitId, GroupHeading: null, Ordinal: 2),
            new IngredientLine(MilkParentId,  60m, GramUnitId, GroupHeading: null, Ordinal: 3),
        ], clock);

        var parent = Recipe.Create(hid, "Nachos", defaultServings: 4, clock).Value;
        parent.ReplaceLines(
            RecipeLineSet.Create(
                ingredients: [new IngredientLine(ChipsId, 200m, GramUnitId, GroupHeading: null, Ordinal: 1)],
                inclusions: [new InclusionLine(sub.Id, Servings: 2m, GroupHeading: null, Ordinal: 2)],
                parent.Id).Value,
            clock);

        return (parent, sub);
    }

    public static IReadOnlyDictionary<Guid, CatalogProduct> Products() =>
        new Dictionary<Guid, CatalogProduct>
        {
            [ChipsId]      = new(ChipsId,      "Tortilla Chips",    TrackStock: true, GramUnitId, null, IsParent: false, []),
            [CashewsId]    = new(CashewsId,    "Cashews",           TrackStock: true, GramUnitId, null, IsParent: false, []),
            [YeastId]      = new(YeastId,      "Nutritional Yeast", TrackStock: true, GramUnitId, null, IsParent: false, []),
            [MilkParentId] = new(MilkParentId, "Plant Milk",        TrackStock: true, GramUnitId, null, IsParent: true,
                [OatMilkId, SoyMilkId]),
            [OatMilkId]    = new(OatMilkId,    "Oat Milk",          TrackStock: true, GramUnitId, MilkParentId, IsParent: false, []),
            [SoyMilkId]    = new(SoyMilkId,    "Soy Milk",          TrackStock: true, GramUnitId, MilkParentId, IsParent: false, []),
        };

    public static IReadOnlyDictionary<Guid, string> UnitCodes() =>
        new Dictionary<Guid, string> { [GramUnitId] = "g" };

    /// <summary>
    /// Every product amply in stock so the render has no shortfall noise. Oat Milk holds more than Soy Milk
    /// so FEFO best-selection auto-selects Oat Milk — the swap test then explicitly picks Soy Milk to prove
    /// the InclusionPickerSelections override lands on the chosen variant, not the auto-selected default.
    /// </summary>
    public static IReadOnlyDictionary<Guid, ProductStock> Stock() =>
        new Dictionary<Guid, ProductStock>
        {
            [ChipsId]   = new(ChipsId,   1000m, GramUnitId, null),
            [CashewsId] = new(CashewsId, 1000m, GramUnitId, null),
            [YeastId]   = new(YeastId,   1000m, GramUnitId, null),
            [OatMilkId] = new(OatMilkId,  500m, GramUnitId, null),
            [SoyMilkId] = new(SoyMilkId,  100m, GramUnitId, null),
        };
}

/// <summary>
/// In-memory <see cref="IRecipeRepository"/> that resolves any of several recipes by id (parent + subs),
/// mirroring the real household-scoped query filter. Needed because the Cook page now reads the EXPANDED
/// recipe via <see cref="RecipeExpansionService"/>, which loads sub-recipes through this port.
/// </summary>
public sealed class FakeMultiRecipeRepository(ITenantContext tenant, params Recipe[] recipes)
    : IRecipeRepository
{
    public Task AddAsync(Recipe r, CancellationToken ct = default) => Task.CompletedTask;

    public Task<Recipe?> GetByIdAsync(RecipeId id, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } hid) return Task.FromResult<Recipe?>(null);
        var match = recipes.FirstOrDefault(r => r.Id == id && r.HouseholdId.Value == hid);
        return Task.FromResult(match);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> NameExistsAsync(HouseholdId householdId, string name, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<Recipe>> ListForBrowseAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Recipe>>([]);

    public Task<IReadOnlySet<RecipeId>> ListRecipeIdsWithPhotoAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<RecipeId>>(new HashSet<RecipeId>());

    public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyDictionary<RecipeId, string>> GetRecipeNamesByIdAsync(
        IReadOnlyList<RecipeId> ids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<RecipeId, string>>(new Dictionary<RecipeId, string>());

    public Task<IReadOnlyList<RecipeInclusionEdge>> ListInclusionEdgesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RecipeInclusionEdge>>([]);

    public Task<IReadOnlySet<RecipeId>> GetIncluderIdsAsync(
        RecipeId subRecipeId, bool transitive = false, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<RecipeId>>(new HashSet<RecipeId>());
}

/// <summary>
/// L4 WebApplicationFactory for the Cook page composition tests. Boots the real Plantry.Web pipeline
/// (including the real <see cref="RecipeExpansionService"/>) but replaces the Postgres-backed seams with
/// the in-memory inclusion fixture. Exposes a <see cref="RecordingFakeCookInventoryConsumer"/> so POST
/// tests can assert which products were (or were not) consumed.
/// </summary>
internal sealed class CookInclusionFactory : WebApplicationFactory<Program>
{
    public Recipe Parent { get; }
    public Recipe Sub { get; }

    public CookInclusionFactory()
    {
        var (parent, sub) = CookInclusionFixture.Build();
        Parent = parent;
        Sub = sub;
    }

    public Guid RecipeId => Parent.Id.Value;

    /// <summary>The '/'-joined InclusionId path of the single inclusion (Nacho Cheese) under the parent.</summary>
    public string InclusionPathKey => Parent.Inclusions.Single().Id.Value.ToString();

    /// <summary>The lineKey ("{pathKey}|{ingredientId}") of a sub ingredient by its product id.</summary>
    public string LineKeyFor(Guid subProductId)
    {
        var ingId = Sub.Ingredients.Single(i => i.ProductId == subProductId).Id.Value;
        return $"{InclusionPathKey}|{ingId}";
    }

    public RecordingFakeCookInventoryConsumer Consumer { get; } = new();

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
                new FakeMultiRecipeRepository(sp.GetRequiredService<ITenantContext>(), Parent, Sub));

            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeCookCatalogReader(CookInclusionFixture.Products(), CookInclusionFixture.UnitCodes()));

            services.RemoveAll<IInventoryStockReader>();
            services.AddSingleton<IInventoryStockReader>(
                new FakeCookStockReader(CookInclusionFixture.Stock()));

            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeCookUnitConverter());
            services.AddFakeQuantityFormatter();

            services.RemoveAll<IInventoryConsumer>();
            services.AddSingleton<IInventoryConsumer>(Consumer);

            services.RemoveAll<ICookEventRepository>();
            services.AddSingleton<ICookEventRepository>(new FakeCookEventRepository());

            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(new FakeTagRepository(new Dictionary<TagId, string>()));

            services.RemoveAll<IPriceReader>();
            services.AddSingleton<IPriceReader>(new FakeDetailPriceReader(new Dictionary<Guid, PricePoint>()));
        });
    }
}
