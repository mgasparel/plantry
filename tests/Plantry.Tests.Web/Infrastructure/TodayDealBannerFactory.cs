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

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// L4 WebApplicationFactory for the Today page Phase-5 <b>deal-review</b> banner (plantry-bpw),
/// in-window variant. Boots the full <c>Plantry.Web</c> pipeline with in-memory fakes for every seam
/// the Today page touches; the real <see cref="Plantry.Deals.Application.BrowseDeals"/> read service runs
/// over an in-memory Deals repository seeded with one <b>Pending, in-window</b> deal.
///
/// Also seeds ONE Ready intake session, so the test can prove the deal banner is <b>additive</b> —
/// it renders alongside the untouched intake banner, not in place of it.
/// </summary>
public sealed class TodayDealBannerFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            TodayBannerCommon.ConfigureAuthAndSeams(services, withIntakeSession: true);
            TodayDealsStubs.RegisterWithPendingDeal(services, inWindow: true);
        });
    }
}

/// <summary>
/// L4 WebApplicationFactory for the deal-review banner (plantry-bpw), <b>all-expired</b> variant. One
/// Pending deal whose window has closed (<c>valid_to</c> in the past) and no intake session — so
/// <see cref="Plantry.Deals.Application.BrowseDeals"/> recomputes zero pending-in-window and the banner
/// stack renders nothing. Proves the count is clock-driven (DD14), never a stamped snapshot.
/// </summary>
public sealed class TodayDealBannerExpiredFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            TodayBannerCommon.ConfigureAuthAndSeams(services, withIntakeSession: false);
            TodayDealsStubs.RegisterWithPendingDeal(services, inWindow: false);
        });
    }
}

/// <summary>
/// Shared registration of the non-Deals seams a Today-page WAF needs (auth + Identity/Inventory/Intake/
/// Recipes/MealPlanning fakes). Factories layer their own Deals seams on top. Mirrors the
/// <see cref="TodayReviewBannerOneFactory"/> wiring so the deal-banner factories exercise the same page.
/// </summary>
internal static class TodayBannerCommon
{
    public static void ConfigureAuthAndSeams(IServiceCollection services, bool withIntakeSession)
    {
        services.AddAuthentication(opts =>
            {
                opts.DefaultScheme = TestAuthHandler.SchemeName;
                opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

        services.RemoveAll<IHouseholdRepository>();
        services.AddSingleton<IHouseholdRepository>(new FakeTodayHouseholdRepository());

        services.RemoveAll<IProductStockRepository>();
        services.AddSingleton<IProductStockRepository>(new FakeTodayStockRepository(hasStock: true));

        services.RemoveAll<ICatalogReadFacade>();
        services.AddSingleton<ICatalogReadFacade>(new FakeTodayCatalogReadFacade());

        services.RemoveAll<IProductConversionProvider>();
        services.AddSingleton<IProductConversionProvider>(new FakeTodayConversionProvider());

        services.RemoveAll<IImportSessionRepository>();
        services.AddSingleton<IImportSessionRepository>(new FakeBannerSessionRepository(
            withIntakeSession
                ? [TodayReviewBannerFixture.BuildReadySession(lineCount: 3, store: "Whole Foods", minutesAgo: 30)]
                : []));

        services.RemoveAll<IRecipeRepository>();
        services.AddScoped<IRecipeRepository>(sp =>
            new FakeBrowseRecipeRepository(sp.GetRequiredService<Plantry.SharedKernel.Tenancy.ITenantContext>(), []));

        services.RemoveAll<ITagRepository>();
        services.AddSingleton<ITagRepository>(new FakeBrowseTagRepository([]));

        services.RemoveAll<ICatalogProductReader>();
        services.AddSingleton<ICatalogProductReader>(
            new FakeBrowseCatalogProductReader(new Dictionary<Guid, CatalogProduct>()));

        services.RemoveAll<IInventoryStockReader>();
        services.AddSingleton<IInventoryStockReader>(
            new FakeBrowseStockReader(new Dictionary<Guid, Plantry.Recipes.Application.ProductStock>()));

        services.RemoveAll<IPriceReader>();
        services.AddSingleton<IPriceReader>(new FakeBrowsePriceReader(new Dictionary<Guid, PricePoint>()));

        services.RemoveAll<IUnitConverter>();
        services.AddSingleton<IUnitConverter>(new FakeBrowseUnitConverter());

        services.RemoveAll<ICatalogWriter>();
        services.AddSingleton<ICatalogWriter>(new FakeCatalogWriter());

        TodayMealPlanningStubs.RegisterNull(services);
    }
}
