using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.Deals.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Deals;

/// <summary>
/// L3 <b>per-subscription unit-of-work isolation</b> (P5-6 / DJ2, plantry-60p9). The whole household shares
/// ONE scoped <see cref="DealsDbContext"/> across every subscription in a cycle. EF keeps entities tracked
/// when <c>SaveChanges</c> throws, so a save-fault in one subscription strands its Added/Deleted
/// <see cref="Deal"/> rows in the shared context; the next subscription's commit would otherwise flush them
/// — inserting the failed flyer's deals and deleting the household's prior Pending deals meant only to be
/// replaced. This runs the real <see cref="IngestFlyer"/> over real RLS-armed Deals repositories against
/// Postgres <b>as app_user</b>, faults the first subscription's deals-save, and proves the second
/// subscription commits <b>only</b> its own work and the faulted subscription's prior Pending deal is intact.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class IngestFlyerSaveFaultIsolationTests(PostgresFixture db) : IAsyncLifetime
{
    // Ordered so the refresh (faulting) subscription is created — and therefore processed — first.
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));

    private HouseholdId _household;
    private Guid _refreshStore; // processed FIRST; its deals.SaveChanges faults on a changed re-pull (refresh path)
    private Guid _newStore;     // processed SECOND; a brand-new import that must commit only its own deal (new path)
    private FlyerImportId _priorImportId = default!;
    private DealId _priorPendingDealId = default!;

    private static readonly DateOnly WindowFrom = new(2026, 7, 1);
    private static readonly DateOnly WindowTo = new(2026, 7, 7);
    private static ValidityWindow Window() => ValidityWindow.Create(WindowFrom, WindowTo).Value;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
        _refreshStore = Guid.NewGuid();
        _newStore = Guid.NewGuid();

        await using var ctx = ArmedContext(_household, out _);

        // Two active subscriptions in the SAME household. refreshStore is subscribed first, so ListActiveAsync
        // (OrderBy CreatedAt) yields it first and IngestFlyer processes it before newStore.
        await ctx.StoreSubscriptions.AddAsync(StoreSubscription.Subscribe(_household, _refreshStore, "M5V0A1", _clock));
        _clock.Advance(TimeSpan.FromSeconds(1));
        await ctx.StoreSubscriptions.AddAsync(StoreSubscription.Subscribe(_household, _newStore, "M5V0A2", _clock));
        await ctx.SaveChangesAsync();

        // A prior Parsed import for refreshStore with one still-Pending deal. A changed re-pull will drop this
        // Pending deal and stage the new one, then fault on save — this deal MUST survive untouched.
        var priorImport = FlyerImport.Start(
            _household, _refreshStore, "refresh-flyer", contentHash: [9, 9], Window(), "{\"v\":1}", _clock);
        priorImport.MarkParsed(pendingCount: 1, _clock);
        await ctx.FlyerImports.AddAsync(priorImport);
        await ctx.SaveChangesAsync(); // persist the import before the deal — the composite FK has no EF navigation

        var priorRaw = new RawDeal("Yogurt", null, null, 3.00m, 1m, null, null, Window());
        var priorDeal = Deal.Stage(
            _household, priorImport.Id, _refreshStore, priorRaw, DealNormalizer.Normalize("Yogurt"),
            MatchProposal.Unmatched(), _clock);
        await ctx.Deals.AddAsync(priorDeal);
        await ctx.SaveChangesAsync();

        _priorImportId = priorImport.Id;
        _priorPendingDealId = priorDeal.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "A deals-save fault in one subscription never leaks into the next subscription's commit (shared-context isolation)")]
    public async Task Save_Fault_In_One_Subscription_Does_Not_Leak_Into_The_Next()
    {
        await using (var ctx = ArmedContext(_household, out var tenant))
        {
            var source = new StubFlyerSource();
            // refreshStore: a CHANGED re-pull (same flyer id, different content) → refresh path → drop the prior
            // Pending "Yogurt" and stage a new one, then deals.SaveChangesAsync faults (the first deals-save).
            source.Enqueue("ref-refresh", new FlyerPullResult(
                "refresh-flyer", Window(), "{\"v\":2}",
                [new RawDeal("Yogurt", null, null, 9.99m, 1m, null, null, Window())]));
            // newStore: a brand-new import → new path → its deals.SaveChangesAsync must commit only "Apples".
            source.Enqueue("ref-new", new FlyerPullResult(
                "new-flyer", Window(), "{\"n\":1}",
                [new RawDeal("Apples", null, null, 2.00m, 1m, null, null, Window())]));

            var storeReader = new StubCatalogStoreReader();
            storeReader.ExternalRefs[_refreshStore] = "ref-refresh";
            storeReader.ExternalRefs[_newStore] = "ref-new";

            var subs = new StoreSubscriptionRepository(ctx);
            var imports = new FlyerImportRepository(ctx);
            var deals = new FaultOnceDealRepository(new DealRepository(ctx)); // first deals-save (refreshStore) throws
            var memories = new DealMatchMemoryRepository(ctx);
            var products = new StubCatalogProductReader();
            var confirm = new ConfirmDeal(deals, memories, products, new StubPriceObservationWriter(), _clock, tenant, NullLogger<ConfirmDeal>.Instance);

            var ingest = new IngestFlyer(
                subs, imports, deals, memories, source, new StubDealMatcher(), storeReader, products, confirm,
                tenant, _clock, NullLogger<IngestFlyer>.Instance);

            var summary = await ingest.RunAsync();

            Assert.True(deals.HasFaulted, "the first deals-save should have faulted");
            Assert.Equal(1, summary.Failed);  // refreshStore's save-fault was isolated
            Assert.Equal(1, summary.Pulled);  // newStore still committed
        }

        // The shared context's tracker was reset at the subscription boundary, so newStore's commit persisted
        // ONLY its own work and none of refreshStore's stranded partial changes.
        await using (var ctx = ArmedContext(_household, out _))
        {
            // refreshStore's prior Pending deal is intact — the faulted refresh's Remove never leaked out.
            var prior = await ctx.Deals.FirstOrDefaultAsync(d => d.Id == _priorPendingDealId);
            Assert.NotNull(prior);
            Assert.Equal(DealStatus.Pending, prior!.Status);
            Assert.Equal(3.00m, prior.Price);

            // refreshStore's newly-staged deal (9.99) was NOT inserted — its Added row never leaked out.
            Assert.DoesNotContain(await ctx.Deals.ToListAsync(), d => d.StoreId == _refreshStore && d.Price == 9.99m);
            var refreshDeals = await ctx.Deals.Where(d => d.StoreId == _refreshStore).ToListAsync();
            Assert.Single(refreshDeals); // only the original Pending deal survives

            // newStore committed exactly its own deal.
            var appleDeals = await ctx.Deals.Where(d => d.StoreId == _newStore).ToListAsync();
            var apple = Assert.Single(appleDeals);
            Assert.Equal("Apples", apple.RawName);
            Assert.Equal(2.00m, apple.Price);

            // refreshStore's prior import is untouched (still Parsed); newStore got a fresh Parsed import.
            var priorImport = await ctx.FlyerImports.FirstAsync(f => f.Id == _priorImportId);
            Assert.Equal(PullStatus.Parsed, priorImport.Status);
            Assert.Contains(await ctx.FlyerImports.ToListAsync(), f => f.StoreId == _newStore && f.Status == PullStatus.Parsed);
        }
    }

    /// <summary>
    /// Builds a DealsDbContext armed for <paramref name="household"/> exactly as the worker does: an app_user
    /// connection (so RLS applies), the RLS connection interceptor bound to a fresh <see cref="ITenantContext"/>,
    /// and the EF query filter via <c>SetHouseholdId</c>.
    /// </summary>
    private DealsDbContext ArmedContext(HouseholdId household, out ITenantContext tenant)
    {
        var armed = new ArmedTenantContext();
        armed.Set(household.Value);
        tenant = armed;

        var options = new DbContextOptionsBuilder<DealsDbContext>()
            .UseNpgsql(db.AppUserConnectionString)
            .AddInterceptors(new HouseholdRlsConnectionInterceptor(armed))
            .Options;
        var ctx = new DealsDbContext(options);
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }
}
