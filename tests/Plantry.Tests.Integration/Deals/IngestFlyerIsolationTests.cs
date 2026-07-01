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
/// L3 <b>multi-tenant isolation</b> — the highest-risk part of P5-6 (DJ2). The worker arms tenancy with no
/// HTTP principal; this proves a pull for household A reads and writes <b>no</b> household-B rows. It runs
/// the real <see cref="IngestFlyer"/> over real, RLS-armed Deals repositories against Postgres <b>as
/// app_user</b> (so the row-level-security policies actually apply, not just the EF filter), exactly as the
/// background worker's per-household scope does.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class IngestFlyerIsolationTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly IClock Clock = SystemClock.Instance;

    private HouseholdId _householdA;
    private HouseholdId _householdB;
    private Guid _storeA;
    private Guid _storeB;
    private FlyerImportId _bImportId = default!;
    private DealId _bDealId = default!;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _householdA = HouseholdId.New();
        _householdB = HouseholdId.New();
        _storeA = Guid.NewGuid();
        _storeB = Guid.NewGuid();

        // Household A: one active subscription to store A.
        await using (var ctx = ArmedContext(_householdA, out _))
        {
            await ctx.StoreSubscriptions.AddAsync(StoreSubscription.Subscribe(_householdA, _storeA, "K1A0B1", Clock));
            await ctx.SaveChangesAsync();
        }

        // Household B: a subscription AND pre-existing ingested rows (a FlyerImport + a Deal) that A must never touch.
        await using (var ctx = ArmedContext(_householdB, out _))
        {
            await ctx.StoreSubscriptions.AddAsync(StoreSubscription.Subscribe(_householdB, _storeB, "M5V0A1", Clock));

            var window = ValidityWindow.Create(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7)).Value;
            var bImport = FlyerImport.Start(_householdB, _storeB, "b-flyer", [9, 9], window, "{\"b\":1}", Clock);
            bImport.MarkParsed(pendingCount: 1, Clock);
            await ctx.FlyerImports.AddAsync(bImport);
            await ctx.SaveChangesAsync(); // persist the import before the deal — the composite FK has no EF navigation to order the inserts

            var normalized = DealNormalizer.Normalize("B Item");
            var raw = new RawDeal("B Item", null, null, 1.00m, 1m, null, null, window);
            var bDeal = Deal.Stage(_householdB, bImport.Id, _storeB, raw, normalized, MatchProposal.Unmatched(), Clock);
            await ctx.Deals.AddAsync(bDeal);
            await ctx.SaveChangesAsync();

            _bImportId = bImport.Id;
            _bDealId = bDeal.Id;
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "A pull for household A writes only A's rows and reads none of B's (RLS, no HTTP principal)")]
    public async Task Pull_For_A_Never_Touches_B()
    {
        // Arm household A exactly as the worker's per-household scope does, and run the real pipeline.
        await using (var ctx = ArmedContext(_householdA, out var tenant))
        {
            var source = new StubFlyerSource();
            var window = ValidityWindow.Create(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7)).Value;
            source.Enqueue("ref-a", new FlyerPullResult(
                "a-flyer", window, "{\"a\":1}",
                [new RawDeal("A Milk", null, null, 3.49m, 1m, null, null, window)]));

            var storeReader = new StubCatalogStoreReader();
            storeReader.ExternalRefs[_storeA] = "ref-a";

            var subs = new StoreSubscriptionRepository(ctx);
            var imports = new FlyerImportRepository(ctx);
            var deals = new DealRepository(ctx);
            var memories = new DealMatchMemoryRepository(ctx);
            var products = new StubCatalogProductReader();
            var confirm = new ConfirmDeal(deals, memories, products, new StubPriceObservationWriter(), Clock, tenant, NullLogger<ConfirmDeal>.Instance);

            var ingest = new IngestFlyer(
                subs, imports, deals, memories, source, new StubDealMatcher(), storeReader, products, confirm,
                tenant, Clock, NullLogger<IngestFlyer>.Instance);

            var summary = await ingest.RunAsync();
            Assert.Equal(1, summary.Pulled);
        }

        // Armed as A: A's new flyer import + deal exist; B's rows are invisible (RLS).
        await using (var ctx = ArmedContext(_householdA, out _))
        {
            var aImport = await ctx.FlyerImports.SingleAsync();
            Assert.Equal(_householdA, aImport.HouseholdId);
            Assert.Equal("a-flyer", aImport.FlyerExternalId);

            var aDeal = await ctx.Deals.SingleAsync();
            Assert.Equal(_householdA, aDeal.HouseholdId);
            Assert.Equal("A Milk", aDeal.RawName);

            Assert.Null(await ctx.FlyerImports.FirstOrDefaultAsync(f => f.Id == _bImportId));
            Assert.Null(await ctx.Deals.FirstOrDefaultAsync(d => d.Id == _bDealId));
        }

        // Armed as B: B's original rows are untouched, and A's new rows are invisible.
        await using (var ctx = ArmedContext(_householdB, out _))
        {
            var bImport = await ctx.FlyerImports.SingleAsync();
            Assert.Equal(_bImportId, bImport.Id);
            Assert.Equal("b-flyer", bImport.FlyerExternalId);

            var bDeal = await ctx.Deals.SingleAsync();
            Assert.Equal(_bDealId, bDeal.Id);
            Assert.Equal("B Item", bDeal.RawName);
        }
    }

    /// <summary>
    /// Builds a DealsDbContext armed for <paramref name="household"/> exactly as the worker does: an
    /// app_user connection (so RLS applies), the RLS connection interceptor bound to a fresh
    /// <see cref="ITenantContext"/>, and the EF query filter via <c>SetHouseholdId</c>.
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
