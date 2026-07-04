using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Identity.Domain;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// L4 WebApplicationFactory base for the Today expiring-soon widget's foot actions (plantry-w1e).
/// Boots the full <c>Plantry.Web</c> pipeline but replaces every Postgres-backed and cross-context
/// seam with in-memory fakes, mirroring the Today review-banner factories.
///
/// <para>The empty stock repository yields <b>zero</b> expiring lots, so the widget never enters its
/// populated state under this factory — that path (and the deep-link landing) is proven end-to-end by
/// <c>TodayExpiringSoonSmokeTests</c>. These L4 factories exist to assert the negative: the
/// "Use these up" action is <b>absent</b> in the two non-populated widget states.</para>
///
/// <list type="bullet">
///   <item><see cref="TodayExpiringWidgetAllClearFactory"/> — stock exists but nothing is expiring →
///     all-clear widget state.</item>
///   <item><see cref="TodayExpiringWidgetColdStartFactory"/> — no stock, no recipes, no pending intake →
///     cold-start widget state.</item>
/// </list>
/// </summary>
public abstract class TodayExpiringWidgetFactoryBase : WebApplicationFactory<Program>
{
    /// <summary>Drives <c>IsColdStart</c>: true → all-clear, false → cold-start.</summary>
    protected abstract bool HasStock { get; }

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

            services.RemoveAll<IHouseholdRepository>();
            services.AddSingleton<IHouseholdRepository>(new FakeTodayHouseholdRepository());

            // Empty lot list → ExpiringSoonAsync returns nothing (never the populated state).
            // HasStock only toggles AnyForHouseholdAsync, which drives IsColdStart.
            services.RemoveAll<IProductStockRepository>();
            services.AddSingleton<IProductStockRepository>(new FakeTodayStockRepository(hasStock: HasStock));

            services.RemoveAll<ICatalogReadFacade>();
            services.AddSingleton<ICatalogReadFacade>(new FakeTodayCatalogReadFacade());

            services.RemoveAll<IProductConversionProvider>();
            services.AddSingleton<IProductConversionProvider>(new FakeTodayConversionProvider());

            // No pending intake sessions → does not lift the household out of cold-start.
            services.RemoveAll<IImportSessionRepository>();
            services.AddSingleton<IImportSessionRepository>(new FakeTodaySessionRepository());

            // No recipes → does not lift the household out of cold-start.
            services.RemoveAll<IRecipeRepository>();
            services.AddScoped<IRecipeRepository>(sp =>
                new FakeBrowseRecipeRepository(sp.GetRequiredService<ITenantContext>(), []));

            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(new FakeBrowseTagRepository([]));

            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeBrowseCatalogProductReader(new Dictionary<Guid, CatalogProduct>()));

            services.RemoveAll<IInventoryStockReader>();
            services.AddSingleton<IInventoryStockReader>(
                new FakeBrowseStockReader(new Dictionary<Guid, Plantry.Recipes.Application.ProductStock>()));

            services.RemoveAll<IPriceReader>();
            services.AddSingleton<IPriceReader>(
                new FakeBrowsePriceReader(new Dictionary<Guid, PricePoint>()));

            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeBrowseUnitConverter());

            services.RemoveAll<ICatalogWriter>();
            services.AddSingleton<ICatalogWriter>(new FakeCatalogWriter());

            // MealPlanning + Deals seams: null/empty — these tests exercise only the expiring widget.
            TodayMealPlanningStubs.RegisterNull(services);
            TodayDealsStubs.RegisterEmpty(services);
        });
    }
}

/// <summary>Stock exists but nothing is expiring → the widget renders its all-clear state.</summary>
public sealed class TodayExpiringWidgetAllClearFactory : TodayExpiringWidgetFactoryBase
{
    protected override bool HasStock => true;
}

/// <summary>No stock, recipes, or pending intake → the widget renders its cold-start state.</summary>
public sealed class TodayExpiringWidgetColdStartFactory : TodayExpiringWidgetFactoryBase
{
    protected override bool HasStock => false;
}
