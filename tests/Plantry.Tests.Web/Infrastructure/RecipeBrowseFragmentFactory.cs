using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// L4 WebApplicationFactory for the recipe Browse page (P2-2c). Boots the real
/// <c>Plantry.Web</c> pipeline (routing, authorization, Razor rendering) but replaces all
/// Postgres-backed and cross-context seams with in-memory fakes:
/// <list type="bullet">
///   <item><see cref="IRecipeRepository"/> — returns three fixture recipes.</item>
///   <item><see cref="ITagRepository"/> — returns fixture tags for filter chips + mini-pills.</item>
///   <item><see cref="ICatalogProductReader"/> — returns the fixture product set.</item>
///   <item><see cref="IInventoryStockReader"/> — returns fixture stock snapshots.</item>
///   <item><see cref="IPriceReader"/> — returns fixture price points (Milk has no price).</item>
///   <item><see cref="IUnitConverter"/> — identity converter (same-unit).</item>
/// </list>
/// No database is touched; rendered HTML is deterministic.
/// </summary>
public class RecipeBrowseFragmentFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// Household display currency the per-recipe cost cells render with (plantry-2x6e.2). Default USD; a derived
    /// factory overrides it to exercise the non-USD symbol path.
    /// </summary>
    protected virtual string DisplayCurrency => "USD";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // Household display currency (plantry-2x6e.2): deterministic fake so the browse cost cells resolve
            // without a real Identity DB.
            services.AddFakeDisplayCurrency(DisplayCurrency);
            services.AddFakeExpiringSoonHorizon();
            // Auth: header-driven test scheme, same pattern as other L4 factories.
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Recipe repository: three fixture recipes.
            services.RemoveAll<IRecipeRepository>();
            services.AddScoped<IRecipeRepository>(sp =>
                new FakeBrowseRecipeRepository(
                    sp.GetRequiredService<ITenantContext>(),
                    RecipeBrowseFixture.BuildRecipes()));

            // Tag repository: fixture tags (Vegetarian, Spicy).
            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(
                new FakeBrowseTagRepository(RecipeBrowseFixture.Tags()));

            // Catalog product reader: fixture products.
            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeBrowseCatalogProductReader(RecipeBrowseFixture.Products()));

            // Inventory stock reader: fixture stock snapshots.
            services.RemoveAll<IInventoryStockReader>();
            services.AddSingleton<IInventoryStockReader>(
                new FakeBrowseStockReader(RecipeBrowseFixture.Stock()));

            // Price reader: fixture price points (Milk has no price → NoCost recipe).
            services.RemoveAll<IPriceReader>();
            services.AddSingleton<IPriceReader>(
                new FakeBrowsePriceReader(RecipeBrowseFixture.Prices()));

            // Unit converter: identity (ingredient unit == product default unit in fixture).
            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeBrowseUnitConverter());

            // Recipe rating repository (plantry-zlwp.1): BrowseRecipesQuery now batch-loads ratings per
            // row; the real RecipeRatingRepository needs a live RecipesDbContext/Postgres connection,
            // so it is replaced here like every other Postgres-backed seam above. Empty by default — no
            // fixture recipe has been rated, so MyStars/HouseholdAvg/RatedCount are null/0 on every row.
            services.RemoveAll<IRecipeRatingRepository>();
            services.AddSingleton<IRecipeRatingRepository>(new FakeBrowseRecipeRatingRepository());

            // AuthorRecipe is registered in Program.cs and requires ICatalogWriter — replaced below.
            services.RemoveAll<ICatalogWriter>();
            services.AddSingleton<ICatalogWriter>(new FakeCatalogWriter());
        });
    }
}

/// <summary>
/// Variant: the browse cost cells rendered for a EUR household (plantry-2x6e.2) — proves the per-recipe cost
/// renders MoneyDisplay's '€' symbol.
/// </summary>
public sealed class RecipeBrowseEurFactory : RecipeBrowseFragmentFactory
{
    protected override string DisplayCurrency => "EUR";
}

/// <summary>
/// In-memory <see cref="ICatalogProductReader"/> for Browse tests — used by
/// <see cref="FulfillmentService"/> to resolve TrackStock flag per product.
/// </summary>
internal sealed class FakeBrowseCatalogProductReader(IReadOnlyDictionary<Guid, CatalogProduct> products)
    : ICatalogProductReader
{
    public Task<CatalogProduct?> FindAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(products.GetValueOrDefault(productId));

    public Task<IReadOnlyList<CatalogProductCandidate>> SearchAsync(string nameQuery, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogProductCandidate>>([]);

    public Task<IReadOnlyDictionary<Guid, CatalogProductSummary>> ResolveSummariesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, CatalogProductSummary> result = productIds
            .Where(products.ContainsKey)
            .Distinct()
            .ToDictionary(id => id, id => new CatalogProductSummary(id, products[id].Name, products[id].TrackStock));
        return Task.FromResult(result);
    }

    public Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
        IReadOnlyList<Guid> unitIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

    public Task<IReadOnlyList<CatalogUnitOption>> ListUnitsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogUnitOption>>([]);

    public Task<IReadOnlyList<CatalogGroupOption>> ListGroupsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogGroupOption>>([]);

    public Task<IReadOnlyList<CatalogCategoryOption>> ListCategoriesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogCategoryOption>>([]);
}

/// <summary>Empty in-memory <see cref="IRecipeRatingRepository"/> — no fixture recipe has been rated.</summary>
internal sealed class FakeBrowseRecipeRatingRepository : IRecipeRatingRepository
{
    public Task AddAsync(RecipeRating rating, CancellationToken ct = default) => Task.CompletedTask;

    public void Remove(RecipeRating rating) { }

    public Task<RecipeRating?> FindAsync(RecipeId recipeId, Guid userId, CancellationToken ct = default) =>
        Task.FromResult<RecipeRating?>(null);

    public Task<IReadOnlyList<RecipeRating>> ListByRecipeAsync(RecipeId recipeId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RecipeRating>>([]);

    public Task<IReadOnlyList<RecipeRating>> ListByRecipeIdsAsync(
        IReadOnlyList<RecipeId> recipeIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RecipeRating>>([]);

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}
