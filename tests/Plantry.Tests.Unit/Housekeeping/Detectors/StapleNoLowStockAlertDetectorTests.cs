using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Unit.Housekeeping;
using Plantry.Web.Housekeeping;

namespace Plantry.Tests.Unit.Housekeeping.Detectors;

/// <summary>
/// L1 unit tests for <see cref="StapleNoLowStockAlertDetector"/> (D4, tidy-up.md §3) over an in-memory
/// <see cref="StockFactsBag"/> — restores the fast coverage the retired fake-port test file provided,
/// including the null-<c>PurchasedAt</c> exclusion and the 90-day lookback-window exclusion that the L3
/// tests in <c>StockDetectorsTests.cs</c> don't independently exercise. Uses the shared
/// <see cref="TestClock"/> pinned to the same 2026-07-22 "today" the L3 tests use.
/// </summary>
public sealed class StapleNoLowStockAlertDetectorTests
{
    private static readonly Guid HouseholdGuid = Guid.NewGuid();
    private static readonly Guid MilkId = Guid.NewGuid();
    private static readonly Guid EachId = Guid.NewGuid();
    private static readonly IClock Clock = new TestClock(new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero));

    private static UnitFact Each => new(EachId, "ea", "each", "count", null, false);
    private static ProductFact Milk => new(MilkId, "Milk", true, EachId);

    private static StockFactsBag BagWithPurchases(decimal? threshold, params DateOnly?[] purchaseDates)
    {
        var lots = purchaseDates
            .Select(d => new StockLotFact(Guid.NewGuid(), MilkId, EachId, 1m, null, d, true))
            .ToArray();
        return new StockFactsBag(
            new Dictionary<Guid, StockProductFact> { [MilkId] = new(MilkId, threshold, lots) },
            new Dictionary<Guid, ProductFact> { [MilkId] = Milk },
            new Dictionary<Guid, UnitFact> { [EachId] = Each },
            new Dictionary<Guid, IReadOnlyList<ConversionFact>>());
    }

    private static StapleNoLowStockAlertDetector BuildDetector(StockFactsBag bag, ITenantContext? tenant = null) =>
        new(new FakeStockFactsReadModel(bag), Clock, tenant ?? new FakeTenantContext(HouseholdGuid));

    [Fact(DisplayName = "3 distinct purchase dates within 90 days, no threshold — produces a finding")]
    public async Task ThreeDistinctDates_NoThreshold_ProducesFinding()
    {
        var bag = BagWithPurchases(null, new DateOnly(2026, 7, 1), new DateOnly(2026, 6, 15), new DateOnly(2026, 5, 25));

        var finding = Assert.Single(await BuildDetector(bag).DetectAsync());

        Assert.Equal(DetectorId.StapleNoLowStockAlert, finding.DetectorId);
        Assert.Equal(MilkId, finding.SubjectId);
        Assert.Equal("Milk", finding.SubjectName);
    }

    [Fact(DisplayName = "Boundary: exactly 2 distinct purchase dates — does NOT fire")]
    public async Task TwoDistinctDates_DoesNotFire()
    {
        var bag = BagWithPurchases(null, new DateOnly(2026, 7, 1), new DateOnly(2026, 6, 15));

        Assert.Empty(await BuildDetector(bag).DetectAsync());
    }

    [Fact(DisplayName = "Threshold already set — never flagged even with frequent purchases")]
    public async Task ThresholdSet_NeverFlagged()
    {
        var bag = BagWithPurchases(2m, new DateOnly(2026, 7, 1), new DateOnly(2026, 6, 15), new DateOnly(2026, 5, 25));

        Assert.Empty(await BuildDetector(bag).DetectAsync());
    }

    [Fact(DisplayName = "Null PurchasedAt entries are ignored — do not count toward the distinct-date total")]
    public async Task NullPurchasedAt_Ignored()
    {
        // Two real dates + two null-dated entries: below the 3-distinct-date threshold even though the
        // entry count alone (4) would suggest it fires.
        var bag = BagWithPurchases(null, new DateOnly(2026, 7, 1), new DateOnly(2026, 6, 15), null, null);

        Assert.Empty(await BuildDetector(bag).DetectAsync());
    }

    [Fact(DisplayName = "Purchase dates outside the 90-day lookback window are excluded")]
    public async Task OutsideLookbackWindow_Excluded()
    {
        // Today is 2026-07-22; 90 days back is 2026-04-23. Two of these three dates fall outside the
        // window, leaving only 1 in-window distinct date — below the threshold.
        var bag = BagWithPurchases(null, new DateOnly(2026, 7, 1), new DateOnly(2026, 1, 1), new DateOnly(2025, 12, 1));

        Assert.Empty(await BuildDetector(bag).DetectAsync());
    }

    [Fact(DisplayName = "Depleted entries still count toward purchase frequency")]
    public async Task DepletedEntries_StillCount()
    {
        var lots = new[]
        {
            new StockLotFact(Guid.NewGuid(), MilkId, EachId, 1m, null, new DateOnly(2026, 7, 1), false),
            new StockLotFact(Guid.NewGuid(), MilkId, EachId, 1m, null, new DateOnly(2026, 6, 15), false),
            new StockLotFact(Guid.NewGuid(), MilkId, EachId, 1m, null, new DateOnly(2026, 5, 25), true),
        };
        var bag = new StockFactsBag(
            new Dictionary<Guid, StockProductFact> { [MilkId] = new(MilkId, null, lots) },
            new Dictionary<Guid, ProductFact> { [MilkId] = Milk },
            new Dictionary<Guid, UnitFact> { [EachId] = Each },
            new Dictionary<Guid, IReadOnlyList<ConversionFact>>());

        Assert.Single(await BuildDetector(bag).DetectAsync());
    }

    [Fact(DisplayName = "No tenant — returns no findings")]
    public async Task NoTenant_ReturnsEmpty()
    {
        var bag = BagWithPurchases(null, new DateOnly(2026, 7, 1), new DateOnly(2026, 6, 15), new DateOnly(2026, 5, 25));

        Assert.Empty(await BuildDetector(bag, new FakeTenantContext(null)).DetectAsync());
    }

    [Fact(DisplayName = "Fingerprint pinning: constant regardless of how many distinct dates differ")]
    public async Task Fingerprint_ConstantAcrossDifferentFactPatterns()
    {
        var findingA = Assert.Single(await BuildDetector(
            BagWithPurchases(null, new DateOnly(2026, 7, 1), new DateOnly(2026, 6, 15), new DateOnly(2026, 5, 25))).DetectAsync());

        var findingB = Assert.Single(await BuildDetector(
            BagWithPurchases(null, new DateOnly(2026, 7, 10), new DateOnly(2026, 6, 1), new DateOnly(2026, 5, 1), new DateOnly(2026, 4, 25))).DetectAsync());

        Assert.Equal(findingA.FactsFingerprint, findingB.FactsFingerprint);
    }
}
