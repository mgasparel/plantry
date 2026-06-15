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
/// Fixture data and in-memory doubles for the L4 Cook confirmation page snapshot tests (P2-3d).
///
/// Scenario: a recipe with two tracked leaf ingredients (Pasta, Tomatoes), one parent-product
/// ingredient (Garlic — two variant children: Fresh and Granule where Granule is unit-incompatible),
/// and one untracked staple (Salt). Tomatoes has a shortfall (200g available, 500g needed).
/// </summary>
public static class CookConfirmFixture
{
    public static readonly Guid HouseholdAId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    public static readonly RecipeId RecipeId = Plantry.Recipes.Domain.RecipeId.From(
        Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"));

    // Leaf product ids.
    public static readonly Guid PastaId         = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid TomatoId        = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid SaltId          = Guid.Parse("33333333-3333-3333-3333-333333333333"); // untracked

    // Parent product + variant children.
    public static readonly Guid GarlicParentId  = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid GarlicFreshId   = Guid.Parse("55555555-5555-5555-5555-555555555555"); // compatible
    public static readonly Guid GarlicGranuleId = Guid.Parse("66666666-6666-6666-6666-666666666666"); // unit-incompatible

    // Units.
    public static readonly Guid GramUnitId = Guid.Parse("aaaaaaaa-1111-0000-0000-000000000001");
    public static readonly Guid EachUnitId = Guid.Parse("aaaaaaaa-2222-0000-0000-000000000002");
    public static readonly Guid TbspUnitId = Guid.Parse("aaaaaaaa-3333-0000-0000-000000000003"); // GarlicGranule default unit

    public static Recipe Build()
    {
        var hid = HouseholdId.From(HouseholdAId);
        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;

        var recipe = Plantry.Recipes.Domain.Recipe.Create(hid, "Garlic Pasta", defaultServings: 4, clock).Value;

        recipe.ReplaceIngredients(
        [
            new IngredientLine(PastaId,        400m, GramUnitId, GroupHeading: null, Ordinal: 1),
            new IngredientLine(TomatoId,       500m, GramUnitId, GroupHeading: null, Ordinal: 2),
            new IngredientLine(GarlicParentId,   3m, EachUnitId, GroupHeading: null, Ordinal: 3),
            new IngredientLine(SaltId, Quantity: null, UnitId: null, GroupHeading: null, Ordinal: 4),
        ], clock);

        return recipe;
    }

    public static IReadOnlyDictionary<Guid, CatalogProduct> Products() =>
        new Dictionary<Guid, CatalogProduct>
        {
            [PastaId]         = new(PastaId,         "Rigatoni",       TrackStock: true,  GramUnitId, null, IsParent: false, []),
            [TomatoId]        = new(TomatoId,         "Canned Tomatoes",TrackStock: true,  GramUnitId, null, IsParent: false, []),
            [SaltId]          = new(SaltId,           "Salt",           TrackStock: false, EachUnitId, null, IsParent: false, []),
            [GarlicParentId]  = new(GarlicParentId,   "Garlic",         TrackStock: true,  EachUnitId, null, IsParent: true,
                [GarlicFreshId, GarlicGranuleId]),
            [GarlicFreshId]   = new(GarlicFreshId,    "Garlic, Fresh",  TrackStock: true,  EachUnitId, GarlicParentId, IsParent: false, []),
            [GarlicGranuleId] = new(GarlicGranuleId,  "Garlic, Granule",TrackStock: true,  TbspUnitId, GarlicParentId, IsParent: false, []),
        };

    public static IReadOnlyDictionary<Guid, string> UnitCodes() =>
        new Dictionary<Guid, string>
        {
            [GramUnitId] = "g",
            [EachUnitId] = "ea",
            [TbspUnitId] = "tbsp",
        };

    /// <summary>
    /// Pasta: 600g (InStock).  Tomatoes: 200g (Shortfall — need 500g).
    /// GarlicFresh: 5 ea (auto-selected best variant).  GarlicGranule: 2 tbsp (unit-incompatible, disabled).
    /// </summary>
    public static IReadOnlyDictionary<Guid, ProductStock> Stock() =>
        new Dictionary<Guid, ProductStock>
        {
            [PastaId]        = new(PastaId,         600m, GramUnitId, null),
            [TomatoId]       = new(TomatoId,         200m, GramUnitId, null), // shortfall
            [GarlicFreshId]  = new(GarlicFreshId,    5m,   EachUnitId, null),
            [GarlicGranuleId]= new(GarlicGranuleId,  2m,   TbspUnitId, null),
        };
}

// ── Fake doubles ──────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Unit converter for Cook L4 tests. Same-unit converts succeed; tbsp→ea fails, making
/// GarlicGranule unit-incompatible with the ingredient's EachUnitId.
/// </summary>
public sealed class FakeCookUnitConverter : IUnitConverter
{
    public Task<Result<decimal>> ConvertAsync(
        Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default)
    {
        if (fromUnitId == toUnitId)
            return Task.FromResult(Result<decimal>.Success(amount));
        return Task.FromResult(Result<decimal>.Failure(
            Error.Custom("Test.NoPath", "No conversion path.")));
    }
}

/// <summary>Catalog reader for Cook L4 tests — returns the fixture product set (with parent/variant tree).</summary>
public sealed class FakeCookCatalogReader(
    IReadOnlyDictionary<Guid, CatalogProduct> products,
    IReadOnlyDictionary<Guid, string> unitCodes) : ICatalogProductReader
{
    public Task<CatalogProduct?> FindAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(products.GetValueOrDefault(productId));

    public Task<IReadOnlyList<CatalogProductCandidate>> SearchAsync(string nameQuery, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogProductCandidate>>([]);

    public Task<IReadOnlyDictionary<Guid, CatalogProductSummary>> ResolveSummariesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, CatalogProductSummary> result = productIds
            .Where(products.ContainsKey).Distinct()
            .ToDictionary(id => id, id => new CatalogProductSummary(id, products[id].Name, products[id].TrackStock));
        return Task.FromResult(result);
    }

    public Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
        IReadOnlyList<Guid> unitIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, string> result = unitIds
            .Where(unitCodes.ContainsKey).Distinct()
            .ToDictionary(id => id, id => unitCodes[id]);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<CatalogUnitOption>> ListUnitsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogUnitOption>>([]);
}

/// <summary>Stock reader for Cook L4 tests.</summary>
public sealed class FakeCookStockReader(IReadOnlyDictionary<Guid, ProductStock> stock)
    : IInventoryStockReader
{
    public Task<ProductStock?> FindStockAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(stock.GetValueOrDefault(productId));

    public Task<IReadOnlyDictionary<Guid, ProductStock>> FindStockBatchAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, ProductStock> result = productIds
            .Where(stock.ContainsKey)
            .ToDictionary(id => id, id => stock[id]);
        return Task.FromResult(result);
    }
}

/// <summary>
/// No-op IInventoryConsumer for Cook L4 tests — returns zero shortfall without touching the DB.
/// </summary>
public sealed class FakeCookInventoryConsumer : IInventoryConsumer
{
    public Task<ConsumeResult> ConsumeAsync(
        Guid productId, decimal quantity, Guid unitId,
        ConsumeReason reason, Guid cookEventId, Guid userId,
        CancellationToken ct = default) =>
        Task.FromResult(new ConsumeResult(ShortfallAmount: 0m, RequestUnitId: unitId));
}

/// <summary>No-op ICookEventRepository for Cook L4 tests.</summary>
public sealed class FakeCookEventRepository : ICookEventRepository
{
    public Task AddAsync(CookEvent e, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<CookEvent>> ListByRecipeAsync(RecipeId recipeId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CookEvent>>([]);

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// L4 WebApplicationFactory for the Cook confirmation page. Boots the real Plantry.Web pipeline
/// but replaces all Postgres-backed seams with deterministic in-memory fakes.
/// </summary>
public sealed class CookConfirmFragmentFactory : WebApplicationFactory<Program>
{
    public Recipe Recipe { get; } = CookConfirmFixture.Build();
    public Guid RecipeId => Recipe.Id.Value;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.RemoveAll<IRecipeRepository>();
            services.AddScoped<IRecipeRepository>(sp =>
                new FakeRecipeRepository(sp.GetRequiredService<ITenantContext>(), Recipe));

            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeCookCatalogReader(CookConfirmFixture.Products(), CookConfirmFixture.UnitCodes()));

            services.RemoveAll<IInventoryStockReader>();
            services.AddSingleton<IInventoryStockReader>(
                new FakeCookStockReader(CookConfirmFixture.Stock()));

            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeCookUnitConverter());

            // Stubs so that CookRecipe (which is sealed, not replaced) can execute in the POST path
            // without a real DB — the L4 snapshot tests only exercise GET, but the WAF must still boot.
            services.RemoveAll<IInventoryConsumer>();
            services.AddSingleton<IInventoryConsumer>(new FakeCookInventoryConsumer());

            services.RemoveAll<ICookEventRepository>();
            services.AddSingleton<ICookEventRepository>(new FakeCookEventRepository());

            // Stubs for services the app registers but the Cook page does not use.
            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(new FakeTagRepository(new Dictionary<TagId, string>()));

            services.RemoveAll<IPriceReader>();
            services.AddSingleton<IPriceReader>(new FakeDetailPriceReader(new Dictionary<Guid, PricePoint>()));
        });
    }
}
