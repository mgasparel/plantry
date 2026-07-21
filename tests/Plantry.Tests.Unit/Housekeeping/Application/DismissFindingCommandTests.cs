using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Housekeeping.Application;
using Plantry.Housekeeping.Domain;
using Plantry.SharedKernel;
using Xunit;

namespace Plantry.Tests.Unit.Housekeeping.Application;

/// <summary>L1 unit tests for <see cref="DismissFindingCommand"/> (tidy-up.md T5).</summary>
public sealed class DismissFindingCommandTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid SubjectId = Guid.CreateVersion7();

    [Fact(DisplayName = "No existing tombstone — inserts a new one with the given fingerprint")]
    public async Task NoExistingTombstone_InsertsNew()
    {
        var dismissals = new FakeDismissalRepository();
        var badgeCache = new FakeTidyUpBadgeCache();
        var command = new DismissFindingCommand(dismissals, badgeCache, new TestClock(), NullLogger<DismissFindingCommand>.Instance);

        await command.ExecuteAsync(Household, DetectorId.StockUnitUnconvertible, SubjectId, "fp-1");

        var stored = Assert.Single(dismissals.All);
        Assert.Equal(Household, stored.HouseholdId);
        Assert.Equal(DetectorId.StockUnitUnconvertible, stored.DetectorId);
        Assert.Equal(SubjectId, stored.SubjectId);
        Assert.Equal("fp-1", stored.FactsFingerprint);
    }

    [Fact(DisplayName = "Existing tombstone at the same key — supersedes in place, does not add a second row")]
    public async Task ExistingTombstoneSameKey_SupersedesInPlace_NoDuplicateRow()
    {
        var dismissals = new FakeDismissalRepository();
        var clock = new TestClock();
        var existing = Dismissal.Create(Household, DetectorId.StockUnitUnconvertible, SubjectId, "fp-old", clock);
        dismissals.Seed(existing);
        var command = new DismissFindingCommand(dismissals, new FakeTidyUpBadgeCache(), clock, NullLogger<DismissFindingCommand>.Instance);

        await command.ExecuteAsync(Household, DetectorId.StockUnitUnconvertible, SubjectId, "fp-new");

        var stored = Assert.Single(dismissals.All); // still exactly one row for this key (T5)
        Assert.Same(existing, stored);
        Assert.Equal("fp-new", stored.FactsFingerprint);
    }

    [Fact(DisplayName = "Invalidates the badge cache after dismissing")]
    public async Task Dismiss_InvalidatesBadgeCache()
    {
        var badgeCache = new FakeTidyUpBadgeCache();
        var command = new DismissFindingCommand(new FakeDismissalRepository(), badgeCache, new TestClock(), NullLogger<DismissFindingCommand>.Instance);

        await command.ExecuteAsync(Household, DetectorId.StockUnitUnconvertible, SubjectId, "fp-1");

        Assert.Equal(1, badgeCache.InvalidateCallCount);
    }
}
