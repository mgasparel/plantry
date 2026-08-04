using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Unit.Housekeeping;
using Plantry.Web.Housekeeping;

namespace Plantry.Tests.Unit.Housekeeping.Detectors;

/// <summary>
/// L1 unit tests for <see cref="StockExpiredDetector"/> (D3, tidy-up.md §3) over an in-memory
/// <see cref="StockFactsBag"/> — restores the fast coverage the retired fake-port test file provided,
/// including the 0-day grace-window boundary and the fingerprint-changes-on-newly-expired-lot direction
/// the L3 tests in <c>StockDetectorsTests.cs</c> don't independently exercise. Uses the shared
/// <see cref="TestClock"/> pinned to the same 2026-07-22 "today" the L3 tests use.
/// </summary>
public sealed class StockExpiredDetectorTests
{
    private static readonly Guid HouseholdGuid = Guid.NewGuid();
    private static readonly Guid YogurtId = Guid.NewGuid();
    private static readonly Guid EachId = Guid.NewGuid();
    private static readonly IClock Clock = new TestClock(new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero));

    private static UnitFact Each => new(EachId, "ea", "each", "count", null, false);
    private static ProductFact Yogurt => new(YogurtId, "Yogurt", true, EachId);

    private static StockFactsBag BagWithLots(params StockLotFact[] lots) => new(
        new Dictionary<Guid, StockProductFact> { [YogurtId] = new(YogurtId, null, lots) },
        new Dictionary<Guid, ProductFact> { [YogurtId] = Yogurt },
        new Dictionary<Guid, UnitFact> { [EachId] = Each },
        new Dictionary<Guid, IReadOnlyList<ConversionFact>>());

    private static StockExpiredDetector BuildDetector(StockFactsBag bag, ITenantContext? tenant = null) =>
        new(new FakeStockFactsReadModel(bag), Clock, tenant ?? new FakeTenantContext(HouseholdGuid));

    [Fact(DisplayName = "Active lot expired before today — produces a finding")]
    public async Task ExpiredLot_ProducesFinding()
    {
        var bag = BagWithLots(new StockLotFact(Guid.NewGuid(), YogurtId, EachId, 2m, new DateOnly(2026, 7, 1), null, true));

        var finding = Assert.Single(await BuildDetector(bag).DetectAsync());

        Assert.Equal(DetectorId.StockExpired, finding.DetectorId);
        Assert.Equal(YogurtId, finding.SubjectId);
        Assert.Equal("Yogurt", finding.SubjectName);
        Assert.Equal("1 lot expired 2026-07-01", finding.Specifics);
        Assert.Equal($"/Pantry/Products/Detail/{YogurtId}", finding.FixUrl);
    }

    [Fact(DisplayName = "Lot expiring exactly today — 0-day grace window, does NOT fire")]
    public async Task ExpiresToday_DoesNotFire()
    {
        var bag = BagWithLots(new StockLotFact(Guid.NewGuid(), YogurtId, EachId, 1m, new DateOnly(2026, 7, 22), null, true));

        Assert.Empty(await BuildDetector(bag).DetectAsync());
    }

    [Fact(DisplayName = "Lot expiring tomorrow — does not fire")]
    public async Task ExpiresTomorrow_DoesNotFire()
    {
        var bag = BagWithLots(new StockLotFact(Guid.NewGuid(), YogurtId, EachId, 1m, new DateOnly(2026, 7, 23), null, true));

        Assert.Empty(await BuildDetector(bag).DetectAsync());
    }

    [Fact(DisplayName = "No expiry date on the lot — never flagged")]
    public async Task NoExpiryDate_DoesNotFire()
    {
        var bag = BagWithLots(new StockLotFact(Guid.NewGuid(), YogurtId, EachId, 1m, null, null, true));

        Assert.Empty(await BuildDetector(bag).DetectAsync());
    }

    [Fact(DisplayName = "Multiple expired lots — Specifics reports the count and the oldest expiry")]
    public async Task MultipleExpiredLots_ReportsCountAndOldest()
    {
        var bag = BagWithLots(
            new StockLotFact(Guid.NewGuid(), YogurtId, EachId, 1m, new DateOnly(2026, 7, 10), null, true),
            new StockLotFact(Guid.NewGuid(), YogurtId, EachId, 1m, new DateOnly(2026, 6, 1), null, true));

        var finding = Assert.Single(await BuildDetector(bag).DetectAsync());

        Assert.Equal("2 lots expired, oldest 2026-06-01", finding.Specifics);
    }

    [Fact(DisplayName = "Depleted lot past expiry — not active, does not fire")]
    public async Task DepletedExpiredLot_DoesNotFire()
    {
        var bag = BagWithLots(new StockLotFact(Guid.NewGuid(), YogurtId, EachId, 1m, new DateOnly(2026, 7, 1), null, false));

        Assert.Empty(await BuildDetector(bag).DetectAsync());
    }

    [Fact(DisplayName = "No tenant — returns no findings")]
    public async Task NoTenant_ReturnsEmpty()
    {
        var bag = BagWithLots(new StockLotFact(Guid.NewGuid(), YogurtId, EachId, 1m, new DateOnly(2026, 7, 1), null, true));

        Assert.Empty(await BuildDetector(bag, new FakeTenantContext(null)).DetectAsync());
    }

    [Fact(DisplayName = "Fingerprint pinning: consuming part of an already-expired lot does NOT change the fingerprint")]
    public async Task Fingerprint_UnaffectedByPartialConsume()
    {
        var entryId = Guid.NewGuid();
        var before = Assert.Single(
            await BuildDetector(BagWithLots(new StockLotFact(entryId, YogurtId, EachId, 5m, new DateOnly(2026, 7, 1), null, true))).DetectAsync());

        // Same StockEntry id (still active, just fewer units remaining) — the fingerprint is built from
        // the expired entry id set, not quantity, so it must not change.
        var after = Assert.Single(
            await BuildDetector(BagWithLots(new StockLotFact(entryId, YogurtId, EachId, 2m, new DateOnly(2026, 7, 1), null, true))).DetectAsync());

        Assert.Equal(before.FactsFingerprint, after.FactsFingerprint);
    }

    [Fact(DisplayName = "Fingerprint pinning: a newly-expired lot changes the fingerprint (reopens dismissal)")]
    public async Task Fingerprint_ChangesWhenAnotherLotExpires()
    {
        var findingOne = Assert.Single(
            await BuildDetector(BagWithLots(new StockLotFact(Guid.NewGuid(), YogurtId, EachId, 1m, new DateOnly(2026, 7, 1), null, true))).DetectAsync());

        var findingTwo = Assert.Single(
            await BuildDetector(BagWithLots(
                new StockLotFact(Guid.NewGuid(), YogurtId, EachId, 1m, new DateOnly(2026, 7, 1), null, true),
                new StockLotFact(Guid.NewGuid(), YogurtId, EachId, 1m, new DateOnly(2026, 6, 1), null, true))).DetectAsync());

        Assert.NotEqual(findingOne.FactsFingerprint, findingTwo.FactsFingerprint);
    }
}
