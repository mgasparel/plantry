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
/// L3 <b>atomic import materialization</b> (P5-6 / DJ2, plantry-pwkm / DD15). One import's write — the
/// <see cref="FlyerImport"/> envelope, its staged <c>Pending</c> <see cref="Deal"/>s, and the <c>Parsed</c>/
/// <c>RecordRepull</c> transition — must commit as ONE unit or not at all. A hard crash mid-materialize (a
/// non-recordable abort between the deal-persist and the status transition) must roll back to <b>nothing</b>,
/// so no partial, still-<c>Pulling</c> import can wedge the <c>(household, store, flyer_external_id)</c> dedup
/// key. This runs the real <see cref="IngestFlyer"/> over real RLS-armed Deals repositories against Postgres
/// <b>as app_user</b>, aborts the deals-save mid-materialize, and proves (a) the whole write rolled back and
/// (b) the very next pull materializes cleanly — no flyer is permanently wedged.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class IngestFlyerAtomicMaterializeTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));

    private HouseholdId _household;
    private Guid _store;

    private static readonly DateOnly WindowFrom = new(2026, 7, 1);
    private static readonly DateOnly WindowTo = new(2026, 7, 7);
    private static ValidityWindow Window() => ValidityWindow.Create(WindowFrom, WindowTo).Value;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
        _store = Guid.NewGuid();

        await using var ctx = ArmedContext(_household, out _);
        await ctx.StoreSubscriptions.AddAsync(StoreSubscription.Subscribe(_household, _store, "M5V0A1", _clock));
        await ctx.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "A hard crash mid-materialize rolls the whole import back (no FlyerImport row) and the next pull recovers cleanly")]
    public async Task HardCrash_During_New_Materialize_RollsBack_And_NextPull_Recovers()
    {
        var source = new StubFlyerSource();
        // Same flyer scripted for both cycles: cycle 1 aborts mid-write; cycle 2 must materialize it cleanly.
        source.Enqueue("flipp-metro", new FlyerPullResult(
            "flyer-1", Window(), "{\"v\":1}",
            [new RawDeal("Bread", null, null, 2.49m, 1m, null, null, Window())]));
        source.Enqueue("flipp-metro", new FlyerPullResult(
            "flyer-1", Window(), "{\"v\":1}",
            [new RawDeal("Bread", null, null, 2.49m, 1m, null, null, Window())]));

        var storeReader = new StubCatalogStoreReader();
        storeReader.ExternalRefs[_store] = "flipp-metro";

        // ── Cycle 1: abort the deals-save mid-materialize (the hard-crash window) ──
        await using (var ctx = ArmedContext(_household, out var tenant))
        {
            var imports = new FlyerImportRepository(ctx);
            var deals = new AbortOnDealsSaveDealRepository(new DealRepository(ctx)); // aborts on the materialize save
            var products = new StubCatalogProductReader();
            var confirm = new ConfirmDeal(deals, new DealMatchMemoryRepository(ctx), products,
                new StubPriceObservationWriter(), _clock, tenant, NullLogger<ConfirmDeal>.Instance);
            var ingest = new IngestFlyer(
                new StoreSubscriptionRepository(ctx), imports, deals, new DealMatchMemoryRepository(ctx),
                source, new StubDealMatcher(), storeReader, products, confirm, tenant, _clock, NullLogger<IngestFlyer>.Instance);

            // The abort surfaces as OperationCanceledException — excluded from Failed-recording, so nothing is
            // recorded (the hard-crash contract), and it propagates out of the cycle.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ingest.RunAsync());
            Assert.True(deals.HasAborted, "the materialize deals-save should have aborted");
        }

        // ── Atomic rollback: the FlyerImport INSERT that ran before the abort was rolled back — NO row survives. ──
        await using (var ctx = ArmedContext(_household, out _))
        {
            Assert.Empty(await ctx.FlyerImports.ToListAsync()); // no wedged, still-Pulling import
            Assert.Empty(await ctx.Deals.ToListAsync());        // no partial deals
        }

        // ── Cycle 2: the next pull is a clean fresh Start — no dedup no-op, no unique-index collision. ──
        await using (var ctx = ArmedContext(_household, out var tenant))
        {
            var imports = new FlyerImportRepository(ctx);
            var deals = new DealRepository(ctx);
            var products = new StubCatalogProductReader();
            var confirm = new ConfirmDeal(deals, new DealMatchMemoryRepository(ctx), products,
                new StubPriceObservationWriter(), _clock, tenant, NullLogger<ConfirmDeal>.Instance);
            var ingest = new IngestFlyer(
                new StoreSubscriptionRepository(ctx), imports, deals, new DealMatchMemoryRepository(ctx),
                source, new StubDealMatcher(), storeReader, products, confirm, tenant, _clock, NullLogger<IngestFlyer>.Instance);

            var summary = await ingest.RunAsync();
            Assert.Equal(1, summary.Pulled);
        }

        await using (var ctx = ArmedContext(_household, out _))
        {
            var import = Assert.Single(await ctx.FlyerImports.ToListAsync());
            Assert.Equal(PullStatus.Parsed, import.Status); // materialized normally on the retry
            var deal = Assert.Single(await ctx.Deals.ToListAsync());
            Assert.Equal("Bread", deal.RawName);
            Assert.Equal(DealStatus.Pending, deal.Status);
            Assert.Equal(import.Id, deal.FlyerImportId);
        }
    }

    [Fact(DisplayName = "A recordable fault mid-materialize rolls the deals back and records a Failed import with error_detail (fresh envelope, no dup-key collision)")]
    public async Task RecordableFault_During_New_Materialize_RollsBackDeals_And_RecordsFailed()
    {
        var source = new StubFlyerSource();
        source.Enqueue("flipp-metro", new FlyerPullResult(
            "flyer-1", Window(), "{\"v\":1}",
            [new RawDeal("Bread", null, null, 2.49m, 1m, null, null, Window())]));

        var storeReader = new StubCatalogStoreReader();
        storeReader.ExternalRefs[_store] = "flipp-metro";

        await using (var ctx = ArmedContext(_household, out var tenant))
        {
            // A NON-OCE fault on the materialize deals-save is a *recordable* failure, not a hard crash: the
            // atomic write rolls back AND MarkImportFailedAsync must record a fresh Pulling → Failed envelope.
            // This drives the recovery path (DiscardStagedChanges + fresh Start) the OCE tests deliberately skip —
            // by this point the envelope was tracked Added/Parsed inside the transaction, so recording Failed must
            // first detach it (else it collides with the fresh Failed row on the dedup unique index) and must use a
            // fresh Start (import.MarkFailed on the already-Parsed aggregate would hit its Pulling guard).
            var deals = new FaultOnceDealRepository(new DealRepository(ctx)); // first deals-save (the materialize save) throws
            var products = new StubCatalogProductReader();
            var confirm = new ConfirmDeal(deals, new DealMatchMemoryRepository(ctx), products,
                new StubPriceObservationWriter(), _clock, tenant, NullLogger<ConfirmDeal>.Instance);
            var ingest = new IngestFlyer(
                new StoreSubscriptionRepository(ctx), new FlyerImportRepository(ctx), deals,
                new DealMatchMemoryRepository(ctx), source, new StubDealMatcher(), storeReader, products, confirm,
                tenant, _clock, NullLogger<IngestFlyer>.Instance);

            var summary = await ingest.RunAsync();

            Assert.True(deals.HasFaulted, "the materialize deals-save should have faulted");
            Assert.Equal(1, summary.Failed); // the recordable fault was isolated to this subscription
            Assert.Equal(0, summary.Pulled);
        }

        // The materialized deals rolled back; exactly one Failed import is recorded with error_detail. It is a
        // fresh envelope — the stranded Parsed envelope was discarded, so there is no (household, store,
        // flyer_external_id) unique-index collision and the Failed transition is valid.
        await using (var ctx = ArmedContext(_household, out _))
        {
            var import = Assert.Single(await ctx.FlyerImports.ToListAsync());
            Assert.Equal(PullStatus.Failed, import.Status);
            Assert.False(string.IsNullOrWhiteSpace(import.ErrorDetail));
            Assert.Empty(await ctx.Deals.ToListAsync()); // no partial deals survived the rollback
        }
    }

    [Fact(DisplayName = "A hard crash mid-refresh rolls the re-pull back, leaving the prior Parsed import and its Pending deals intact")]
    public async Task HardCrash_During_Repull_Refresh_RollsBack_LeavingPriorStateIntact()
    {
        // Seed a prior Parsed import with one still-Pending deal — a changed re-pull will drop this Pending deal
        // and stage a new one, then abort on save. The prior state MUST survive untouched (atomic rollback).
        FlyerImportId priorImportId;
        DealId priorDealId;
        await using (var ctx = ArmedContext(_household, out _))
        {
            var priorImport = FlyerImport.Start(
                _household, _store, "flyer-1", contentHash: [1, 1], Window(), "{\"v\":1}", _clock);
            priorImport.MarkParsed(pendingCount: 1, _clock);
            await ctx.FlyerImports.AddAsync(priorImport);
            await ctx.SaveChangesAsync(); // import before its deal — the composite FK has no EF navigation

            var priorDeal = Deal.Stage(
                _household, priorImport.Id, _store, new RawDeal("Milk", null, null, 3.00m, 1m, null, null, Window()),
                DealNormalizer.Normalize("Milk"), MatchProposal.Unmatched(), _clock);
            await ctx.Deals.AddAsync(priorDeal);
            await ctx.SaveChangesAsync();
            priorImportId = priorImport.Id;
            priorDealId = priorDeal.Id;
        }

        var source = new StubFlyerSource();
        // A CHANGED re-pull (same flyer id, new content + new price) → refresh path → drop prior "Milk" and stage
        // a new one, then the deals-save aborts.
        source.Enqueue("flipp-metro", new FlyerPullResult(
            "flyer-1", Window(), "{\"v\":2}",
            [new RawDeal("Milk", null, null, 9.99m, 1m, null, null, Window())]));

        var storeReader = new StubCatalogStoreReader();
        storeReader.ExternalRefs[_store] = "flipp-metro";

        await using (var ctx = ArmedContext(_household, out var tenant))
        {
            var deals = new AbortOnDealsSaveDealRepository(new DealRepository(ctx));
            var products = new StubCatalogProductReader();
            var confirm = new ConfirmDeal(deals, new DealMatchMemoryRepository(ctx), products,
                new StubPriceObservationWriter(), _clock, tenant, NullLogger<ConfirmDeal>.Instance);
            var ingest = new IngestFlyer(
                new StoreSubscriptionRepository(ctx), new FlyerImportRepository(ctx), deals,
                new DealMatchMemoryRepository(ctx), source, new StubDealMatcher(), storeReader, products, confirm,
                tenant, _clock, NullLogger<IngestFlyer>.Instance);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ingest.RunAsync());
            Assert.True(deals.HasAborted, "the refresh deals-save should have aborted");
        }

        // The refresh rolled back wholesale: the prior import is still Parsed with its ORIGINAL content hash and
        // window, and its prior Pending deal survives at its ORIGINAL price — no half-refreshed, wedged state.
        await using (var ctx = ArmedContext(_household, out _))
        {
            var import = Assert.Single(await ctx.FlyerImports.ToListAsync());
            Assert.Equal(priorImportId, import.Id);
            Assert.Equal(PullStatus.Parsed, import.Status);
            Assert.Equal(new byte[] { 1, 1 }, import.ContentHash); // unchanged — RecordRepull rolled back

            var deal = Assert.Single(await ctx.Deals.ToListAsync());
            Assert.Equal(priorDealId, deal.Id);
            Assert.Equal(DealStatus.Pending, deal.Status);
            Assert.Equal(3.00m, deal.Price);                       // original price — the 9.99 stage never committed
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
