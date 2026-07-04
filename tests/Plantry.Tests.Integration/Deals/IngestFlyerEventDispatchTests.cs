using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.Deals.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.Events;
using Xunit;

namespace Plantry.Tests.Integration.Deals;

/// <summary>
/// L3 proof that domain-event dispatch is <b>transaction-aware</b> (plantry-jvzk). The
/// <see cref="FlyerImportedEvent"/> that <see cref="FlyerImport.MarkParsed"/> raises is emitted from INSIDE
/// <see cref="IngestFlyer"/>'s explicit two-save materialization transaction. The dispatch interceptor pair
/// (<see cref="DomainEventDispatchInterceptor"/> buffering on an inner save +
/// <see cref="DomainEventCommitDispatchInterceptor"/> flushing on commit) must dispatch it only once that
/// transaction COMMITS, and never when it rolls back — closing the pre-commit phantom-event window. These
/// tests run the real <see cref="IngestFlyer"/> and a real RLS-armed Deals context against Postgres as
/// <c>app_user</c>, with a capturing handler behind the real dispatcher and interceptors.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class IngestFlyerEventDispatchTests(PostgresFixture db) : IAsyncLifetime
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

        await using var ctx = ArmedContext(_household, buffer: null, dispatcher: null, out _);
        await ctx.StoreSubscriptions.AddAsync(StoreSubscription.Subscribe(_household, _store, "M5V0A1", _clock));
        await ctx.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "Rollback: an aborted import materialization dispatches NO FlyerImportedEvent (no phantom on rollback)")]
    public async Task Aborted_Materialization_Dispatches_No_Event()
    {
        var (handler, dispatcher, buffer) = NewDispatch();

        var source = SingleBreadFlyer();
        var storeReader = StoreReader();

        await using (var ctx = ArmedContext(_household, buffer, dispatcher, out var tenant))
        {
            var imports = new FlyerImportRepository(ctx);
            // Abort the deals-save that runs AFTER the FlyerImport (Parsed) INSERT — i.e. after MarkParsed's
            // FlyerImportedEvent was already drained + buffered — so the whole transaction rolls back.
            var deals = new AbortOnDealsSaveDealRepository(new DealRepository(ctx));
            var ingest = NewIngest(ctx, imports, deals, source, storeReader, tenant);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ingest.RunAsync());
            Assert.True(deals.HasAborted, "the materialize deals-save should have aborted");
        }

        // The transaction rolled back: no FlyerImport row survives AND the buffered FlyerImportedEvent was
        // never flushed — a phantom event for a write that never happened is exactly what the fix prevents.
        Assert.Empty(handler.Events);
        await using var verify = ArmedContext(_household, buffer: null, dispatcher: null, out _);
        Assert.Empty(await verify.FlyerImports.ToListAsync());
    }

    [Fact(DisplayName = "Commit: a materialized import dispatches its FlyerImportedEvent exactly once, AFTER the transaction commits")]
    public async Task Committed_Materialization_Dispatches_Event_Once_Post_Commit()
    {
        var (handler, dispatcher, buffer) = NewDispatch();

        // When the handler fires, open a FRESH connection and count the committed FlyerImport rows. Pre-commit
        // dispatch would see 0 (the row is not yet committed); post-commit dispatch sees 1. This is the
        // definitive ordering proof.
        var rowsVisibleAtDispatch = -1;
        handler.OnHandle = async importedEvent =>
        {
            _ = importedEvent;
            await using var probe = ArmedContext(_household, buffer: null, dispatcher: null, out _);
            rowsVisibleAtDispatch = await probe.FlyerImports.CountAsync();
        };

        var source = SingleBreadFlyer();
        var storeReader = StoreReader();

        await using (var ctx = ArmedContext(_household, buffer, dispatcher, out var tenant))
        {
            var ingest = NewIngest(ctx, new FlyerImportRepository(ctx), new DealRepository(ctx), source, storeReader, tenant);
            var summary = await ingest.RunAsync();
            Assert.Equal(1, summary.Pulled);
        }

        var evt = Assert.Single(handler.Events);
        Assert.Equal(_store, evt.StoreId);
        Assert.Equal(1, evt.PendingCount);          // one Pending "Bread" deal at parse time
        Assert.Equal(1, rowsVisibleAtDispatch);     // the FlyerImport was already committed when the handler ran
    }

    [Fact(DisplayName = "Single save regression: an event raised outside any explicit transaction still dispatches post-commit")]
    public async Task Single_Save_Outside_Transaction_Still_Dispatches()
    {
        var (handler, dispatcher, buffer) = NewDispatch();

        await using var ctx = ArmedContext(_household, buffer, dispatcher, out _);

        var import = FlyerImport.Start(_household, _store, "flyer-single", contentHash: [7], Window(), "{\"v\":1}", _clock);
        await ctx.FlyerImports.AddAsync(import);
        await ctx.SaveChangesAsync();               // INSERT (Pulling) — no domain event raised yet
        Assert.Empty(handler.Events);

        var mark = import.MarkParsed(pendingCount: 3, _clock);
        Assert.True(mark.IsSuccess);
        await ctx.SaveChangesAsync();               // bare save, no explicit transaction → immediate dispatch

        var evt = Assert.Single(handler.Events);
        Assert.Equal(3, evt.PendingCount);
    }

    [Fact(DisplayName = "Re-entrancy: a further SaveChanges after dispatch does not re-dispatch the already-cleared batch")]
    public async Task Further_Save_Does_Not_Redispatch_The_Cleared_Batch()
    {
        var (handler, dispatcher, buffer) = NewDispatch();

        await using var ctx = ArmedContext(_household, buffer, dispatcher, out _);

        var import = FlyerImport.Start(_household, _store, "flyer-reentrant", contentHash: [8], Window(), "{\"v\":1}", _clock);
        await ctx.FlyerImports.AddAsync(import);
        await ctx.SaveChangesAsync();

        import.MarkParsed(pendingCount: 2, _clock);
        await ctx.SaveChangesAsync();               // dispatches once, then CLEARS the event from the aggregate
        Assert.Single(handler.Events);

        // A subsequent save on the same context (the same thing a handler that itself called SaveChanges would
        // trigger) re-runs the interceptor. Because the batch was cleared before dispatch, the still-tracked
        // FlyerImport carries no events, so nothing is re-dispatched.
        var other = FlyerImport.Start(_household, _store, "flyer-reentrant-2", contentHash: [9], Window(), "{\"v\":1}", _clock);
        await ctx.FlyerImports.AddAsync(other);
        await ctx.SaveChangesAsync();

        Assert.Single(handler.Events);              // still exactly one — no re-dispatch of the cleared batch
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static (CapturingFlyerImportedHandler Handler, IDomainEventDispatcher Dispatcher, TransactionalDomainEventBuffer Buffer) NewDispatch()
    {
        var handler = new CapturingFlyerImportedHandler();
        var dispatcher = new DomainEventDispatcher(new ServiceCollection()
            .AddSingleton<IDomainEventHandler<FlyerImportedEvent>>(handler)
            .BuildServiceProvider());
        return (handler, dispatcher, new TransactionalDomainEventBuffer());
    }

    private StubFlyerSource SingleBreadFlyer()
    {
        var source = new StubFlyerSource();
        source.Enqueue("flipp-metro", new FlyerPullResult(
            "flyer-1", Window(), "{\"v\":1}",
            [new RawDeal("Bread", null, null, 2.49m, 1m, null, null, Window())]));
        return source;
    }

    private StubCatalogStoreReader StoreReader()
    {
        var reader = new StubCatalogStoreReader();
        reader.ExternalRefs[_store] = "flipp-metro";
        return reader;
    }

    private IngestFlyer NewIngest(
        DealsDbContext ctx, IFlyerImportRepository imports, IDealRepository deals,
        StubFlyerSource source, StubCatalogStoreReader storeReader, ITenantContext tenant)
    {
        var products = new StubCatalogProductReader();
        var confirm = new ConfirmDeal(deals, new DealMatchMemoryRepository(ctx), products,
            new StubPriceObservationWriter(), _clock, tenant, NullLogger<ConfirmDeal>.Instance);
        return new IngestFlyer(
            new StoreSubscriptionRepository(ctx), imports, deals, new DealMatchMemoryRepository(ctx),
            source, new StubDealMatcher(), storeReader, products, confirm, tenant, _clock,
            NullLogger<IngestFlyer>.Instance);
    }

    /// <summary>
    /// Builds a DealsDbContext armed for <paramref name="household"/> as the worker does (app_user connection
    /// + RLS interceptor + query filter). When <paramref name="buffer"/> and <paramref name="dispatcher"/> are
    /// supplied, the transaction-aware dispatch interceptor pair is wired too, sharing that single buffer.
    /// </summary>
    private DealsDbContext ArmedContext(
        HouseholdId household, TransactionalDomainEventBuffer? buffer, IDomainEventDispatcher? dispatcher, out ITenantContext tenant)
    {
        var armed = new ArmedTenantContext();
        armed.Set(household.Value);
        tenant = armed;

        var options = new DbContextOptionsBuilder<DealsDbContext>()
            .UseNpgsql(db.AppUserConnectionString)
            .AddInterceptors(new HouseholdRlsConnectionInterceptor(armed));

        if (buffer is not null && dispatcher is not null)
            options.AddInterceptors(
                new DomainEventDispatchInterceptor(dispatcher, buffer),
                new DomainEventCommitDispatchInterceptor(dispatcher, buffer));

        var ctx = new DealsDbContext(options.Options);
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }

    private sealed class CapturingFlyerImportedHandler : IDomainEventHandler<FlyerImportedEvent>
    {
        public List<FlyerImportedEvent> Events { get; } = [];

        /// <summary>Optional side effect run per handled event (e.g. probing committed state at dispatch time).</summary>
        public Func<FlyerImportedEvent, Task>? OnHandle { get; set; }

        public async Task HandleAsync(FlyerImportedEvent domainEvent, CancellationToken ct = default)
        {
            Events.Add(domainEvent);
            if (OnHandle is not null)
                await OnHandle(domainEvent);
        }
    }
}
