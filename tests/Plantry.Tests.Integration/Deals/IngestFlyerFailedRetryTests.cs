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
/// L3 regression for plantry-0l05: a materialize fault records a <see cref="PullStatus.Failed"/> flyer_import
/// that no longer poison-pills the dedup key. Under the partial unique index (<c>WHERE status='parsed'</c>),
/// only Parsed envelopes occupy <c>(household, store, flyer_external_id)</c>, so a later fault-free cycle
/// re-ingests the same flyer as a fresh pull while every Failed attempt is retained as an audit row. Runs the
/// real <see cref="IngestFlyer"/> over RLS-armed Deals repositories against Postgres <b>as app_user</b>, so the
/// partial index is genuinely exercised. Mirrors the fault injection of
/// <see cref="IngestFlyerSaveFaultIsolationTests"/>.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class IngestFlyerFailedRetryTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));

    private HouseholdId _household;
    private Guid _store;

    private static readonly DateOnly WindowFrom = new(2026, 7, 1);
    private static readonly DateOnly WindowTo = new(2026, 7, 7);
    private static ValidityWindow Window() => ValidityWindow.Create(WindowFrom, WindowTo).Value;

    private const string FlyerId = "flyer-8006782";
    private const string ExternalRef = "ref-store";

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

    [Fact(DisplayName = "A materialize fault records Failed but does NOT poison the dedup key: the next fault-free cycle re-ingests the flyer (plantry-0l05)")]
    public async Task Failed_Import_Does_Not_Block_Retry_Next_Cycle()
    {
        // ── Cycle 1: inject a materialize fault → the flyer is recorded Failed, no deals persisted. ──
        await using (var ctx = ArmedContext(_household, out var tenant))
        {
            var deals = new FaultOnceDealRepository(new DealRepository(ctx));
            var summary = await RunCycleAsync(ctx, tenant, deals, contentVersion: 1);

            Assert.True(deals.HasFaulted, "cycle 1's materialize save should have faulted");
            Assert.Equal(1, summary.Failed);
            Assert.Equal(0, summary.Pulled);
        }

        Guid failedImportId;
        await using (var ctx = ArmedContext(_household, out _))
        {
            var failed = Assert.Single(await ctx.FlyerImports.Where(f => f.StoreId == _store).ToListAsync());
            Assert.Equal(PullStatus.Failed, failed.Status);
            Assert.Empty(await ctx.Deals.Where(d => d.StoreId == _store).ToListAsync());
            failedImportId = failed.Id.Value;
        }

        _clock.Advance(TimeSpan.FromDays(1));

        // ── Cycle 2: fault removed → FindParsedByDedupKey returns null (Failed-only history), so the flyer is
        //    re-ingested as a fresh Parsed import with its deal materialized. ──
        await using (var ctx = ArmedContext(_household, out var tenant))
        {
            var deals = new DealRepository(ctx);
            var summary = await RunCycleAsync(ctx, tenant, deals, contentVersion: 2);

            Assert.Equal(0, summary.Failed);
            Assert.Equal(1, summary.Pulled);
        }

        await using (var ctx = ArmedContext(_household, out _))
        {
            var imports = await ctx.FlyerImports.Where(f => f.StoreId == _store).OrderBy(f => f.CreatedAt).ToListAsync();
            Assert.Equal(2, imports.Count); // the retained Failed audit row + the fresh Parsed row

            var stillFailed = Assert.Single(imports, i => i.Id.Value == failedImportId);
            Assert.Equal(PullStatus.Failed, stillFailed.Status); // original Failed row survives untouched

            var parsed = Assert.Single(imports, i => i.Status == PullStatus.Parsed);
            Assert.NotEqual(failedImportId, parsed.Id.Value);

            // The deal from cycle 2 materialized against the fresh Parsed import.
            var deal = Assert.Single(await ctx.Deals.Where(d => d.StoreId == _store).ToListAsync());
            Assert.Equal("Milk", deal.RawName);
            Assert.Equal(parsed.Id.Value, deal.FlyerImportId!.Value.Value);

            // The successful retry advanced the subscription's pull bookkeeping (the pull was NOT silently skipped).
            var sub = await ctx.StoreSubscriptions.SingleAsync(s => s.StoreId == _store);
            Assert.Equal(FlyerId, sub.LastFlyerExternalId);
            Assert.NotNull(sub.LastPulledAt);
        }
    }

    [Fact(DisplayName = "Two consecutive faulted cycles append two retained Failed rows for the same dedup key (plantry-0l05)")]
    public async Task Two_Faulted_Cycles_Append_Two_Failed_Rows()
    {
        await using (var ctx = ArmedContext(_household, out var tenant))
        {
            var deals = new FaultOnceDealRepository(new DealRepository(ctx));
            var summary = await RunCycleAsync(ctx, tenant, deals, contentVersion: 1);
            Assert.Equal(1, summary.Failed);
        }

        _clock.Advance(TimeSpan.FromDays(1));

        await using (var ctx = ArmedContext(_household, out var tenant))
        {
            var deals = new FaultOnceDealRepository(new DealRepository(ctx));
            var summary = await RunCycleAsync(ctx, tenant, deals, contentVersion: 2);
            Assert.Equal(1, summary.Failed); // the Failed-only history retried, then faulted again
        }

        await using (var ctx = ArmedContext(_household, out _))
        {
            var failed = await ctx.FlyerImports
                .Where(f => f.StoreId == _store && f.Status == PullStatus.Failed)
                .ToListAsync();
            Assert.Equal(2, failed.Count); // both attempts retained under the partial index (no unique collision)
        }
    }

    private async Task<IngestSummary> RunCycleAsync(
        DealsDbContext ctx, ITenantContext tenant, IDealRepository deals, int contentVersion)
    {
        var source = new StubFlyerSource();
        source.Enqueue(ExternalRef, new FlyerPullResult(
            FlyerId, Window(), $"{{\"v\":{contentVersion}}}",
            [new RawDeal("Milk", null, null, 2.49m, 1m, null, null, Window())]));

        var storeReader = new StubCatalogStoreReader();
        storeReader.ExternalRefs[_store] = ExternalRef;

        var subs = new StoreSubscriptionRepository(ctx);
        var imports = new FlyerImportRepository(ctx);
        var memories = new DealMatchMemoryRepository(ctx);
        var products = new StubCatalogProductReader();
        var confirm = new ConfirmDeal(deals, memories, products, new StubPriceObservationWriter(), _clock, tenant, NullLogger<ConfirmDeal>.Instance);

        var ingest = new IngestFlyer(
            subs, imports, deals, memories, source, new StubDealMatcher(), storeReader, products, confirm,
            tenant, _clock, NullLogger<IngestFlyer>.Instance);

        return await ingest.RunAsync();
    }

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
