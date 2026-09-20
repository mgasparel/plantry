using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Unit.Housekeeping;
using Plantry.Composition.Infrastructure;
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
        // LowStockThresholds mirrors StockProductFact.LowStockThreshold here — StockFactsReadModel keeps
        // the two in sync from the same low_stock_rule query (see its Query 1b comment); a hand-built bag
        // must preserve that invariant for GroupKey's threshold check to agree with StockProductFact's.
        var thresholds = threshold is { } t
            ? new Dictionary<Guid, decimal> { [MilkId] = t }
            : new Dictionary<Guid, decimal>();
        return new StockFactsBag(
            new Dictionary<Guid, StockProductFact> { [MilkId] = new(MilkId, threshold, lots) },
            new Dictionary<Guid, ProductFact> { [MilkId] = Milk },
            new Dictionary<Guid, UnitFact> { [EachId] = Each },
            new Dictionary<Guid, IReadOnlyList<ConversionFact>>(),
            thresholds);
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

    // ── Parent fold (plantry-oh27.3) ─────────────────────────────────────────

    [Fact(DisplayName = "Frequent purchases spread across variants with no own rule produce one finding on the parent")]
    public async Task VariantsWithoutOwnRule_FrequentAcrossVariants_ProducesFindingOnParent()
    {
        var parentId = Guid.NewGuid();
        var variantAId = Guid.NewGuid();
        var variantBId = Guid.NewGuid();

        var parent = new ProductFact(parentId, "Bubly", false, EachId, IsParent: true);
        var variantA = new ProductFact(variantAId, "Bubly Orange", true, EachId, ParentProductId: parentId);
        var variantB = new ProductFact(variantBId, "Bubly Lime", true, EachId, ParentProductId: parentId);

        // Neither variant is purchased 3+ times on its own, but the UNION across both variants is.
        var lotsA = new[]
        {
            new StockLotFact(Guid.NewGuid(), variantAId, EachId, 1m, null, new DateOnly(2026, 7, 1), true),
        };
        var lotsB = new[]
        {
            new StockLotFact(Guid.NewGuid(), variantBId, EachId, 1m, null, new DateOnly(2026, 6, 15), true),
            new StockLotFact(Guid.NewGuid(), variantBId, EachId, 1m, null, new DateOnly(2026, 5, 25), true),
        };

        var bag = new StockFactsBag(
            new Dictionary<Guid, StockProductFact>
            {
                [variantAId] = new(variantAId, null, lotsA),
                [variantBId] = new(variantBId, null, lotsB),
            },
            new Dictionary<Guid, ProductFact> { [parentId] = parent, [variantAId] = variantA, [variantBId] = variantB },
            new Dictionary<Guid, UnitFact> { [EachId] = Each },
            new Dictionary<Guid, IReadOnlyList<ConversionFact>>());

        var finding = Assert.Single(await BuildDetector(bag).DetectAsync());

        Assert.Equal(parentId, finding.SubjectId);
        Assert.Equal("Bubly", finding.SubjectName);
    }

    [Fact(DisplayName = "A variant with its own rule is never folded into its parent — no finding when the parent alone would qualify")]
    public async Task VariantWithOwnRule_NotFoldedIntoParent()
    {
        var parentId = Guid.NewGuid();
        var variantId = Guid.NewGuid();

        var parent = new ProductFact(parentId, "Bubly", false, EachId, IsParent: true);
        var variant = new ProductFact(variantId, "Bubly Orange", true, EachId, ParentProductId: parentId);

        var lots = new[]
        {
            new StockLotFact(Guid.NewGuid(), variantId, EachId, 1m, null, new DateOnly(2026, 7, 1), true),
            new StockLotFact(Guid.NewGuid(), variantId, EachId, 1m, null, new DateOnly(2026, 6, 15), true),
            new StockLotFact(Guid.NewGuid(), variantId, EachId, 1m, null, new DateOnly(2026, 5, 25), true),
        };

        var bag = new StockFactsBag(
            new Dictionary<Guid, StockProductFact> { [variantId] = new(variantId, 2m, lots) },
            new Dictionary<Guid, ProductFact> { [parentId] = parent, [variantId] = variant },
            new Dictionary<Guid, UnitFact> { [EachId] = Each },
            new Dictionary<Guid, IReadOnlyList<ConversionFact>>(),
            new Dictionary<Guid, decimal> { [variantId] = 2m });

        // The variant already has its own rule (2m) — it stays its own group and is filtered out by the
        // "group already has a threshold" check, exactly like a leaf with its own rule. It must never be
        // folded into the parent's group (the parent has no rule of its own here).
        Assert.Empty(await BuildDetector(bag).DetectAsync());
    }

    [Fact(DisplayName = "Parent already has a rule — frequent variant purchases do not fire")]
    public async Task ParentWithOwnRule_VariantPurchases_DoNotFire()
    {
        var parentId = Guid.NewGuid();
        var variantId = Guid.NewGuid();

        var parent = new ProductFact(parentId, "Bubly", false, EachId, IsParent: true);
        var variant = new ProductFact(variantId, "Bubly Orange", true, EachId, ParentProductId: parentId);

        var lots = new[]
        {
            new StockLotFact(Guid.NewGuid(), variantId, EachId, 1m, null, new DateOnly(2026, 7, 1), true),
            new StockLotFact(Guid.NewGuid(), variantId, EachId, 1m, null, new DateOnly(2026, 6, 15), true),
            new StockLotFact(Guid.NewGuid(), variantId, EachId, 1m, null, new DateOnly(2026, 5, 25), true),
        };

        var bag = new StockFactsBag(
            new Dictionary<Guid, StockProductFact> { [variantId] = new(variantId, null, lots) },
            new Dictionary<Guid, ProductFact> { [parentId] = parent, [variantId] = variant },
            new Dictionary<Guid, UnitFact> { [EachId] = Each },
            new Dictionary<Guid, IReadOnlyList<ConversionFact>>(),
            new Dictionary<Guid, decimal> { [parentId] = 4m }); // parent-only rule — never on a stock row

        Assert.Empty(await BuildDetector(bag).DetectAsync());
    }
}
