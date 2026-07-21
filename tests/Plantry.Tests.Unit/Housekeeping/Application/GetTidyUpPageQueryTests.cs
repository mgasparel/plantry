using Plantry.Housekeeping.Application;
using Plantry.Housekeeping.Domain;
using Plantry.SharedKernel;
using Xunit;

namespace Plantry.Tests.Unit.Housekeeping.Application;

/// <summary>
/// L1 unit tests for <see cref="GetTidyUpPageQuery"/> — detector fan-out, dismissal matching
/// (open vs. dismissed split by key+fingerprint), severity/detector ordering, empty-group
/// suppression, and the badge-cache refresh (tidy-up.md §4/T2/T5/T6).
/// </summary>
public sealed class GetTidyUpPageQueryTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid ProductA = Guid.CreateVersion7();
    private static readonly Guid ProductB = Guid.CreateVersion7();

    private static Finding MakeFinding(DetectorId detectorId, Guid subjectId, string fingerprint = "fp-1") =>
        new(detectorId, subjectId, "Subject", "specifics", "consequence", "/fix", "Fix", fingerprint);

    private static GetTidyUpPageQuery Build(
        IEnumerable<IProblemDetector> detectors, FakeDismissalRepository? dismissals = null,
        FakeTidyUpBadgeCache? badgeCache = null, FakeTenantContext? tenant = null) =>
        new(detectors, dismissals ?? new FakeDismissalRepository(), badgeCache ?? new FakeTidyUpBadgeCache(),
            tenant ?? new FakeTenantContext(Household.Value));

    [Fact(DisplayName = "No household in tenant context — returns Empty without calling any detector")]
    public async Task NoHousehold_ReturnsEmpty()
    {
        var calledCount = 0;
        var detector = new CountingDetector(() => calledCount++);
        var query = Build([detector], tenant: new FakeTenantContext(null));

        var result = await query.ExecuteAsync();

        Assert.True(result.IsAllTidy);
        Assert.Empty(result.Dismissed);
        Assert.Equal(0, result.OpenCount);
        Assert.Equal(0, calledCount);
    }

    [Fact(DisplayName = "A detector with findings and no matching tombstone — produces an open group")]
    public async Task OpenFinding_NoTombstone_ProducesOpenGroup()
    {
        var finding = MakeFinding(DetectorId.StockUnitUnconvertible, ProductA);
        var detector = new FakeDetector(DetectorId.StockUnitUnconvertible, Severity.BehaviorAffecting, [finding]);
        var query = Build([detector]);

        var result = await query.ExecuteAsync();

        var group = Assert.Single(result.Groups);
        Assert.Equal(DetectorId.StockUnitUnconvertible, group.DetectorId);
        var row = Assert.Single(group.Rows);
        Assert.Equal(ProductA, row.Finding.SubjectId);
        Assert.Empty(result.Dismissed);
        Assert.Equal(1, result.OpenCount);
    }

    [Fact(DisplayName = "A tombstone whose fingerprint matches the finding — suppresses it into Dismissed, not Groups")]
    public async Task MatchingTombstone_SuppressesIntoDismissed()
    {
        var finding = MakeFinding(DetectorId.StockUnitUnconvertible, ProductA, fingerprint: "fp-1");
        var detector = new FakeDetector(DetectorId.StockUnitUnconvertible, Severity.BehaviorAffecting, [finding]);
        var dismissals = new FakeDismissalRepository();
        dismissals.Seed(Dismissal.Create(Household, DetectorId.StockUnitUnconvertible, ProductA, "fp-1", new TestClock()));
        var query = Build([detector], dismissals);

        var result = await query.ExecuteAsync();

        Assert.Empty(result.Groups); // empty groups don't render (T2)
        Assert.Equal(0, result.OpenCount);
        var dismissedRow = Assert.Single(result.Dismissed);
        Assert.Equal(ProductA, dismissedRow.Finding.SubjectId);
    }

    [Fact(DisplayName = "Reopen-on-fact-change: a stale tombstone (different fingerprint) does not suppress — finding reopens as open")]
    public async Task StaleTombstone_DifferentFingerprint_FindingReopens()
    {
        var finding = MakeFinding(DetectorId.StockUnitUnconvertible, ProductA, fingerprint: "fp-NEW");
        var detector = new FakeDetector(DetectorId.StockUnitUnconvertible, Severity.BehaviorAffecting, [finding]);
        var dismissals = new FakeDismissalRepository();
        dismissals.Seed(Dismissal.Create(Household, DetectorId.StockUnitUnconvertible, ProductA, "fp-OLD", new TestClock()));
        var query = Build([detector], dismissals);

        var result = await query.ExecuteAsync();

        var group = Assert.Single(result.Groups);
        Assert.Single(group.Rows);
        Assert.Equal(1, result.OpenCount);
        Assert.Empty(result.Dismissed); // the stale tombstone does not count as a dismissed finding either
    }

    [Fact(DisplayName = "Groups ordered severity-first (BehaviorAffecting before Advisory), then by detector id")]
    public async Task Groups_OrderedBySeverityThenDetectorId()
    {
        var advisory = new FakeDetector(
            new DetectorId("z-advisory"), Severity.Advisory, [MakeFinding(new DetectorId("z-advisory"), ProductA)]);
        var behaviorZ = new FakeDetector(
            new DetectorId("z-behavior"), Severity.BehaviorAffecting, [MakeFinding(new DetectorId("z-behavior"), ProductA)]);
        var behaviorA = new FakeDetector(
            new DetectorId("a-behavior"), Severity.BehaviorAffecting, [MakeFinding(new DetectorId("a-behavior"), ProductB)]);

        var query = Build([advisory, behaviorZ, behaviorA]);

        var result = await query.ExecuteAsync();

        Assert.Equal(3, result.Groups.Count);
        Assert.Equal("a-behavior", result.Groups[0].DetectorId.Value);
        Assert.Equal("z-behavior", result.Groups[1].DetectorId.Value);
        Assert.Equal("z-advisory", result.Groups[2].DetectorId.Value);
    }

    [Fact(DisplayName = "A detector that returns no findings at all — produces no group")]
    public async Task DetectorWithNoFindings_ProducesNoGroup()
    {
        var detector = new FakeDetector(DetectorId.RecipeConversionGap, Severity.BehaviorAffecting, []);
        var query = Build([detector]);

        var result = await query.ExecuteAsync();

        Assert.True(result.IsAllTidy);
    }

    [Fact(DisplayName = "Refreshes the badge cache with the fresh open count")]
    public async Task RefreshesBadgeCache_WithFreshOpenCount()
    {
        var findingA = MakeFinding(DetectorId.StockUnitUnconvertible, ProductA);
        var findingB = MakeFinding(DetectorId.RecipeConversionGap, ProductB);
        var detectors = new IProblemDetector[]
        {
            new FakeDetector(DetectorId.StockUnitUnconvertible, Severity.BehaviorAffecting, [findingA]),
            new FakeDetector(DetectorId.RecipeConversionGap, Severity.BehaviorAffecting, [findingB]),
        };
        var badgeCache = new FakeTidyUpBadgeCache();
        var query = Build(detectors, badgeCache: badgeCache);

        var result = await query.ExecuteAsync();

        Assert.Equal(2, result.OpenCount);
        Assert.Equal(2, await badgeCache.TryGetAsync(Household));
    }

    [Fact(DisplayName = "Dismissed list is ordered most-recently-dismissed first")]
    public async Task Dismissed_OrderedMostRecentFirst()
    {
        var clock = new TestClock();
        var findingA = MakeFinding(DetectorId.StockUnitUnconvertible, ProductA, "fp-a");
        var findingB = MakeFinding(DetectorId.RecipeConversionGap, ProductB, "fp-b");
        var detectors = new IProblemDetector[]
        {
            new FakeDetector(DetectorId.StockUnitUnconvertible, Severity.BehaviorAffecting, [findingA]),
            new FakeDetector(DetectorId.RecipeConversionGap, Severity.BehaviorAffecting, [findingB]),
        };
        var dismissals = new FakeDismissalRepository();
        dismissals.Seed(Dismissal.Create(Household, DetectorId.StockUnitUnconvertible, ProductA, "fp-a", clock));
        clock.Advance(TimeSpan.FromDays(1));
        dismissals.Seed(Dismissal.Create(Household, DetectorId.RecipeConversionGap, ProductB, "fp-b", clock));

        var query = Build(detectors, dismissals);
        var result = await query.ExecuteAsync();

        Assert.Equal(2, result.Dismissed.Count);
        Assert.Equal(ProductB, result.Dismissed[0].Finding.SubjectId); // dismissed later → first
        Assert.Equal(ProductA, result.Dismissed[1].Finding.SubjectId);
    }

    private sealed class CountingDetector(Action onCalled) : IProblemDetector
    {
        public DetectorId Id => DetectorId.StockUnitUnconvertible;
        public Severity Severity => Severity.BehaviorAffecting;
        public string GroupTitle => "Group";
        public string GroupConsequence => "Consequence";
        public string IconName => "i-scale";

        public Task<IReadOnlyList<Finding>> DetectAsync(CancellationToken ct = default)
        {
            onCalled();
            return Task.FromResult<IReadOnlyList<Finding>>([]);
        }
    }
}
