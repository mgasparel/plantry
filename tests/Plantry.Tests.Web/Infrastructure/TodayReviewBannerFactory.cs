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
using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// L4 WebApplicationFactory for the Today page review-banner stack (plantry-yb6), one-session variant.
/// Boots the full <c>Plantry.Web</c> pipeline but replaces all Postgres-backed and cross-context seams
/// with in-memory fakes.
///
/// Session state: ONE Ready intake session (3 lines, "Whole Foods", ~30 min ago) → one banner rendered.
/// </summary>
public sealed class TodayReviewBannerOneFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.AddFakeExpiringSoonHorizon();
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
            // hasStock=true so IsColdStart=false
            services.AddSingleton<IProductStockRepository>(new FakeTodayStockRepository(hasStock: true));

            services.RemoveAll<ICatalogReadFacade>();
            services.AddSingleton<ICatalogReadFacade>(new FakeTodayCatalogReadFacade());

            services.RemoveAll<IProductConversionProvider>();
            services.AddSingleton<IProductConversionProvider>(new FakeTodayConversionProvider());

            // ── Intake — one Ready session ────────────────────────────────────
            services.RemoveAll<IImportSessionRepository>();
            services.AddSingleton<IImportSessionRepository>(
                new FakeBannerSessionRepository([
                    TodayReviewBannerFixture.BuildReadySession(lineCount: 3, store: "Whole Foods", minutesAgo: 30)
                ]));

            // PendingReviewQuery is registered as Scoped in Program.cs — it resolves via IImportSessionRepository.
            // No override needed here; it will pick up the FakeBannerSessionRepository above.

            // ── Recipes ───────────────────────────────────────────────────────
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

            // ── MealPlanning seams (plantry-zp7) — Today page now loads planned meals ─
            // Null stubs: these banner tests only exercise the banner stack, not the meals band.
            TodayMealPlanningStubs.RegisterNull(services);

            // ── Deals seams (plantry-bpw) — empty → no deal banner ───────────
            TodayDealsStubs.RegisterEmpty(services);
        });
    }
}

/// <summary>
/// L4 WebApplicationFactory for the Today page review-banner stack (plantry-yb6), many-session variant.
/// TWO Ready intake sessions → two banners rendered.
/// </summary>
public sealed class TodayReviewBannerManyFactory : WebApplicationFactory<Program>
{
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

            services.RemoveAll<IProductStockRepository>();
            services.AddSingleton<IProductStockRepository>(new FakeTodayStockRepository(hasStock: true));

            services.RemoveAll<ICatalogReadFacade>();
            services.AddSingleton<ICatalogReadFacade>(new FakeTodayCatalogReadFacade());

            services.RemoveAll<IProductConversionProvider>();
            services.AddSingleton<IProductConversionProvider>(new FakeTodayConversionProvider());

            services.RemoveAll<IImportSessionRepository>();
            services.AddSingleton<IImportSessionRepository>(
                new FakeBannerSessionRepository([
                    TodayReviewBannerFixture.BuildReadySession(lineCount: 5, store: "Costco",      minutesAgo: 60),
                    TodayReviewBannerFixture.BuildReadySession(lineCount: 2, store: "Trader Joe's", minutesAgo: 10)
                ]));

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

            // ── MealPlanning seams (plantry-zp7) ─────────────────────────────
            TodayMealPlanningStubs.RegisterNull(services);

            // ── Deals seams (plantry-bpw) — empty → no deal banner ───────────
            TodayDealsStubs.RegisterEmpty(services);
        });
    }
}

/// <summary>
/// L4 WebApplicationFactory for the Today page review-banner stack (plantry-yb6), no-session variant.
/// NO pending sessions → banner stack renders nothing (no chrome).
/// Uses hasStock=true and recipes=empty to keep IsColdStart=false (board renders, but banners are absent).
/// </summary>
public sealed class TodayReviewBannerNoneFactory : WebApplicationFactory<Program>
{
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

            services.RemoveAll<IProductStockRepository>();
            services.AddSingleton<IProductStockRepository>(new FakeTodayStockRepository(hasStock: true));

            services.RemoveAll<ICatalogReadFacade>();
            services.AddSingleton<ICatalogReadFacade>(new FakeTodayCatalogReadFacade());

            services.RemoveAll<IProductConversionProvider>();
            services.AddSingleton<IProductConversionProvider>(new FakeTodayConversionProvider());

            services.RemoveAll<IImportSessionRepository>();
            // Empty — no pending sessions
            services.AddSingleton<IImportSessionRepository>(
                new FakeBannerSessionRepository([]));

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

            // ── MealPlanning seams (plantry-zp7) ─────────────────────────────
            TodayMealPlanningStubs.RegisterNull(services);

            // ── Deals seams (plantry-bpw) — empty → no deal banner ───────────
            TodayDealsStubs.RegisterEmpty(services);
        });
    }
}
