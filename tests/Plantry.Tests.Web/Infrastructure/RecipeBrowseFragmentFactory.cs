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
public sealed class RecipeBrowseFragmentFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
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

            // AuthorRecipe is registered in Program.cs and requires ICatalogWriter — replaced below.
            services.RemoveAll<ICatalogWriter>();
            services.AddSingleton<ICatalogWriter>(new FakeCatalogWriter());
        });
    }
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
}
