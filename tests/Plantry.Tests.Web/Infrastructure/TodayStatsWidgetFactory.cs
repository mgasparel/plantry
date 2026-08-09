using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Identity.Domain;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.Pantry.Application;
using Plantry.Pantry.Domain;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// L4 WebApplicationFactory base for the Today stats widget (plantry-h9z9). Mirrors
/// <see cref="TodayExpiringWidgetFactoryBase"/>'s shape (full pipeline, in-memory fakes for every seam);
/// the one seam these two subclasses vary is <see cref="IWasteJournalReader"/> — a factory can supply
/// one that reports a discard (so the "days since anything expired" chip renders) or the always-empty
/// default (no chips, just the rotating fact).
///
/// <list type="bullet">
///   <item><see cref="TodayStatsWidgetColdStartFactory"/> — no stock, no recipes, no pending intake →
///     the whole board (including the stats widget) is absent.</item>
///   <item><see cref="TodayStatsWidgetNoChipsFactory"/> — stock exists, empty waste reader → widget
///     renders with a rotating fact but zero streak chips.</item>
///   <item><see cref="TodayStatsWidgetWithChipsFactory"/> — stock exists, waste reader reports a recent
///     discard → widget renders with at least one streak chip.</item>
/// </list>
/// </summary>
public abstract class TodayStatsWidgetFactoryBase : WebApplicationFactory<Program>
{
    protected abstract bool HasStock { get; }
    protected virtual IWasteJournalReader WasteReader => new NullWasteJournalReader();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.AddFakeExpiringSoonHorizon();

            // Pin the host clock to the same fixed instant WasteReader's fixture data (below) is keyed
            // off — the WAF-hosted SUT and the seeded fixture must resolve the identical "now", not two
            // independent reads of the real clock, or the "days since anything expired" day-count would
            // drift by a day whenever a test happens to straddle midnight (the same hazard
            // MealPlanFragmentFactory.cs pins IClock for). Reuses the existing shared test instant rather
            // than introducing a second one.
            services.RemoveAll<IClock>();
            services.AddScoped<IClock>(_ => new FixedClock(MealPlanningTestClock.Instant));

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
            services.AddSingleton<IProductStockRepository>(new FakeTodayStockRepository(hasStock: HasStock));

            services.RemoveAll<ICatalogReadFacade>();
            services.AddSingleton<ICatalogReadFacade>(new FakeTodayCatalogReadFacade());

            services.RemoveAll<IProductConversionProvider>();
            services.AddSingleton<IProductConversionProvider>(new FakeTodayConversionProvider());

            services.RemoveAll<IImportSessionRepository>();
            services.AddSingleton<IImportSessionRepository>(new FakeTodaySessionRepository());

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

            TodayMealPlanningStubs.RegisterNull(services);
            TodayDealsStubs.RegisterEmpty(services);

            services.RemoveAll<IWasteJournalReader>();
            services.AddSingleton(WasteReader);
        });
    }
}

/// <summary>No stock, recipes, or pending intake → cold-start; the whole board (stats widget included)
/// is absent from the rendered page.</summary>
public sealed class TodayStatsWidgetColdStartFactory : TodayStatsWidgetFactoryBase
{
    protected override bool HasStock => false;
}

/// <summary>Stock exists, waste reader reports nothing ever discarded → the widget renders with a
/// rotating fact and zero streak chips.</summary>
public sealed class TodayStatsWidgetNoChipsFactory : TodayStatsWidgetFactoryBase
{
    protected override bool HasStock => true;
}

/// <summary>Stock exists, waste reader reports a discard 4 days ago (relative to the pinned host clock,
/// <see cref="MealPlanningTestClock.Instant"/>) → the widget renders at least one streak chip ("4 days
/// since anything expired").</summary>
public sealed class TodayStatsWidgetWithChipsFactory : TodayStatsWidgetFactoryBase
{
    protected override bool HasStock => true;
    protected override IWasteJournalReader WasteReader => new FixedDiscardWasteJournalReader(
        MealPlanningTestClock.Instant.AddDays(-4));
}

/// <summary>Reports a fixed most-recent-discard timestamp and zero recent-discard count — enough to
/// drive the "days since anything expired" chip without needing a real journal.</summary>
internal sealed class FixedDiscardWasteJournalReader(DateTimeOffset lastDiscard) : IWasteJournalReader
{
    public Task<int> CountDiscardedSinceAsync(DateTimeOffset since, CancellationToken ct = default) =>
        Task.FromResult(0);
    public Task<DateTimeOffset?> MostRecentDiscardAsync(CancellationToken ct = default) =>
        Task.FromResult<DateTimeOffset?>(lastDiscard);
}
