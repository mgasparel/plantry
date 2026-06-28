using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Identity.Domain;
using Plantry.Intake.Domain;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// L4 WebApplicationFactory for the Today page cook-now picks band (plantry-81g).
/// Boots the full <c>Plantry.Web</c> pipeline but replaces all Postgres-backed and
/// cross-context seams with in-memory fakes.
///
/// Wired services:
/// <list type="bullet">
///   <item><see cref="IHouseholdRepository"/> — single fixture household.</item>
///   <item><see cref="IProductStockRepository"/> — has stock (hasStock=true → not cold start).</item>
///   <item><see cref="IImportSessionRepository"/> — no pending sessions.</item>
///   <item><see cref="ICatalogReadFacade"/> + <see cref="IProductConversionProvider"/> — passthrough fakes
///     (InventoryQueryService uses them for expiry labels; we supply stubs).</item>
///   <item><see cref="IRecipeRepository"/> — three fixture recipes (Pasta Carbonara, Veggie Stir, Smoothie Bowl).</item>
///   <item><see cref="ITagRepository"/> — empty tag list (Today picks don't display tags).</item>
///   <item><see cref="ICatalogProductReader"/> — fixture products (TrackStock flags for FulfillmentService).</item>
///   <item><see cref="IInventoryStockReader"/> — fixture stock snapshots.</item>
///   <item><see cref="IPriceReader"/> — empty (cost is not shown in cook-now picks).</item>
///   <item><see cref="IUnitConverter"/> — identity (ingredient unit == product unit in fixture).</item>
/// </list>
/// No database is touched; rendered HTML is deterministic (GUIDs scrubbed by Verify).
/// </summary>
public sealed class TodayCookNowFragmentFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // ── Auth ─────────────────────────────────────────────────────────
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // ── Identity / Household ─────────────────────────────────────────
            services.RemoveAll<IHouseholdRepository>();
            services.AddSingleton<IHouseholdRepository>(new FakeTodayHouseholdRepository());

            // ── Inventory seams ───────────────────────────────────────────────
            services.RemoveAll<IProductStockRepository>();
            services.AddSingleton<IProductStockRepository>(new FakeTodayStockRepository(hasStock: true));

            services.RemoveAll<ICatalogReadFacade>();
            services.AddSingleton<ICatalogReadFacade>(new FakeTodayCatalogReadFacade());

            services.RemoveAll<IProductConversionProvider>();
            services.AddSingleton<IProductConversionProvider>(new FakeTodayConversionProvider());

            // ── Intake ────────────────────────────────────────────────────────
            services.RemoveAll<IImportSessionRepository>();
            services.AddSingleton<IImportSessionRepository>(new FakeTodaySessionRepository());

            // ── Recipes ───────────────────────────────────────────────────────
            services.RemoveAll<IRecipeRepository>();
            services.AddScoped<IRecipeRepository>(sp =>
                new FakeBrowseRecipeRepository(
                    sp.GetRequiredService<ITenantContext>(),
                    TodayCookNowFixture.BuildRecipes()));

            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(
                new FakeBrowseTagRepository(TodayCookNowFixture.Tags()));

            // ── BrowseRecipesQuery cross-context seams ────────────────────────
            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeBrowseCatalogProductReader(TodayCookNowFixture.Products()));

            services.RemoveAll<IInventoryStockReader>();
            services.AddSingleton<IInventoryStockReader>(
                new FakeBrowseStockReader(TodayCookNowFixture.Stock()));

            services.RemoveAll<IPriceReader>();
            services.AddSingleton<IPriceReader>(
                new FakeBrowsePriceReader(TodayCookNowFixture.Prices()));

            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeBrowseUnitConverter());

            // CatalogWriter required by AuthorRecipeHandler (registered in Program.cs).
            services.RemoveAll<ICatalogWriter>();
            services.AddSingleton<ICatalogWriter>(new FakeCatalogWriter());
        });
    }
}
