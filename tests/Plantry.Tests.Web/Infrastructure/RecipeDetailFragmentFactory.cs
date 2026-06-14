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
/// L4 WebApplicationFactory for the recipe Detail page. Boots the real <c>Plantry.Web</c> pipeline
/// (routing, authorization, Razor rendering) but replaces all Postgres-backed seams the Detail
/// page depends on — the recipe repository, the tag repository, the catalog product reader, the
/// inventory stock reader, the price reader, and the unit converter — with in-memory fakes, and
/// swaps cookie auth for a header-driven test scheme. No database is touched; rendered HTML is deterministic.
///
/// <para>Default scenario (used by the base snapshot tests): mixed fulfillment status —
/// Pasta InStock, Tomatoes Low, Garlic Missing, Salt Untracked; Partial cost (Garlic un-priced).</para>
/// </summary>
public sealed class RecipeDetailFragmentFactory : WebApplicationFactory<Program>
{
    /// <summary>The recipe used in all Detail snapshots; expose it so tests can construct the URL.</summary>
    public Recipe Recipe { get; } = RecipeDetailFixture.Build();

    public Guid RecipeId => Recipe.Id.Value;

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Non-Development: skips startup migrations/seeding and the Dev-pages gate.
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // Auth: header-driven test scheme mirrors ReviewFragmentFactory.
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Recipe repository: returns the fixture recipe for the owning household.
            services.RemoveAll<IRecipeRepository>();
            services.AddScoped<IRecipeRepository>(sp =>
                new FakeRecipeRepository(sp.GetRequiredService<ITenantContext>(), Recipe));

            // Tag repository: resolves the fixture's known tag id → name mapping.
            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(
                new FakeTagRepository(RecipeDetailFixture.TagNames()));

            // Catalog product reader: returns the fixture product set + unit codes.
            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeCatalogProductReader(RecipeDetailFixture.Products(), RecipeDetailFixture.UnitCodes()));

            // Inventory stock reader: mixed statuses (Pasta=InStock, Tomatoes=Low, Garlic=Missing).
            services.RemoveAll<IInventoryStockReader>();
            services.AddSingleton<IInventoryStockReader>(
                new FakeDetailStockReader(RecipeDetailFixture.Stock(Today)));

            // Price reader: Pasta + Tomatoes priced, Garlic not → Partial.
            services.RemoveAll<IPriceReader>();
            services.AddSingleton<IPriceReader>(
                new FakeDetailPriceReader(RecipeDetailFixture.Prices()));

            // Unit converter: identity (ingredient unit == product default unit in fixture).
            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeDetailUnitConverter());
        });
    }
}
