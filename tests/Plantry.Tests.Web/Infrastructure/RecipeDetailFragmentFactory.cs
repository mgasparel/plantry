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
/// (routing, authorization, Razor rendering) but replaces the three Postgres-backed seams the Detail
/// page depends on — the recipe repository, the tag repository, and the catalog product reader —
/// with in-memory fakes, and swaps cookie auth for a header-driven test scheme. No database is
/// touched, so the rendered HTML is deterministic.
/// </summary>
public sealed class RecipeDetailFragmentFactory : WebApplicationFactory<Program>
{
    /// <summary>The recipe used in all Detail snapshots; expose it so tests can construct the URL.</summary>
    public Recipe Recipe { get; } = RecipeDetailFixture.Build();

    public Guid RecipeId => Recipe.Id.Value;

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
        });
    }
}
