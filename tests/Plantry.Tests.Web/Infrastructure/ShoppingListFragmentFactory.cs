using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Shopping.Application;
using Plantry.Shopping.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// L4 WebApplicationFactory for the Shopping page (P2-Sc). Boots the real
/// <c>Plantry.Web</c> pipeline with all Postgres-backed and cross-context seams
/// replaced by in-memory fakes:
/// <list type="bullet">
///   <item><see cref="IShoppingListRepository"/> — returns the fixture list.</item>
///   <item><see cref="IShoppingCatalogReader"/> — returns fixture product/unit data.</item>
///   <item><see cref="IShoppingPantryReader"/> — returns fixture pantry stock levels (plantry-juh).</item>
///   <item><see cref="ShoppingListQueryService"/> — real service over the fakes above.</item>
/// </list>
/// No database is touched; rendered HTML is deterministic.
/// </summary>
public sealed class ShoppingListFragmentFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // Auth: header-driven test scheme.
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            var list = ShoppingListFixture.BuildList();
            var summaries = ShoppingListFixture.ProductSummaries();
            var unitCodes = ShoppingListFixture.UnitCodes();
            var candidates = ShoppingListFixture.ProductCandidates();
            var stockLevels = ShoppingListFixture.StockLevels();

            // Shopping repository: one fixture list.
            services.RemoveAll<IShoppingListRepository>();
            services.AddScoped<IShoppingListRepository>(sp =>
                new FakeShoppingRepository(sp.GetRequiredService<ITenantContext>(), list));

            // Shopping catalog reader: fixture products/units.
            services.RemoveAll<IShoppingCatalogReader>();
            services.AddSingleton<IShoppingCatalogReader>(
                new FakeShoppingCatalogReader(summaries, unitCodes, candidates));

            // Shopping pantry reader: fixture on-hand stock levels (plantry-juh).
            services.RemoveAll<IShoppingPantryReader>();
            services.AddSingleton<IShoppingPantryReader>(
                new FakeShoppingPantryReaderForSnapshots(stockLevels));

            // Re-register ShoppingListQueryService and PantrySuggestionService so they pick up the fakes.
            services.RemoveAll<ShoppingListQueryService>();
            services.AddScoped<ShoppingListQueryService>();
            services.RemoveAll<PantrySuggestionService>();
            services.AddScoped<PantrySuggestionService>();
        });
    }
}

/// <summary>
/// In-memory <see cref="IShoppingPantryReader"/> for the Shopping L4 snapshot tests.
/// Returns the fixture pantry stock levels registered in <see cref="ShoppingListFixture.StockLevels"/>.
/// </summary>
internal sealed class FakeShoppingPantryReaderForSnapshots(
    IReadOnlyDictionary<Guid, ShoppingPantryStockLevel> levels)
    : IShoppingPantryReader
{
    public Task<IReadOnlyDictionary<Guid, ShoppingPantryStockLevel>> GetStockLevelsAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, ShoppingPantryStockLevel> result = productIds
            .Where(levels.ContainsKey)
            .ToDictionary(id => id, id => levels[id]);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<ShoppingPantryStockLevel>> GetLowStockProductsAsync(
        CancellationToken ct = default)
    {
        IReadOnlyList<ShoppingPantryStockLevel> result = levels.Values
            .Where(l => l.IsLow)
            .ToList();
        return Task.FromResult(result);
    }
}
