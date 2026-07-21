using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Housekeeping.Application;
using Plantry.Housekeeping.Domain;
using Plantry.SharedKernel;
using Xunit;

namespace Plantry.Tests.Unit.Housekeeping.Application;

/// <summary>L1 unit tests for <see cref="RestoreFindingCommand"/> (tidy-up.md T5: "Restore deletes the tombstone").</summary>
public sealed class RestoreFindingCommandTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid SubjectId = Guid.CreateVersion7();

    [Fact(DisplayName = "Existing tombstone — deletes it outright")]
    public async Task ExistingTombstone_DeletesIt()
    {
        var dismissals = new FakeDismissalRepository();
        dismissals.Seed(Dismissal.Create(Household, DetectorId.StockUnitUnconvertible, SubjectId, "fp-1", new TestClock()));
        var command = new RestoreFindingCommand(dismissals, new FakeTidyUpBadgeCache(), NullLogger<RestoreFindingCommand>.Instance);

        await command.ExecuteAsync(Household, DetectorId.StockUnitUnconvertible, SubjectId);

        Assert.Empty(dismissals.All);
    }

    [Fact(DisplayName = "No existing tombstone — is a harmless no-op")]
    public async Task NoExistingTombstone_NoOp()
    {
        var dismissals = new FakeDismissalRepository();
        var command = new RestoreFindingCommand(dismissals, new FakeTidyUpBadgeCache(), NullLogger<RestoreFindingCommand>.Instance);

        await command.ExecuteAsync(Household, DetectorId.StockUnitUnconvertible, SubjectId);

        Assert.Empty(dismissals.All);
    }

    [Fact(DisplayName = "Invalidates the badge cache after restoring")]
    public async Task Restore_InvalidatesBadgeCache()
    {
        var dismissals = new FakeDismissalRepository();
        dismissals.Seed(Dismissal.Create(Household, DetectorId.StockUnitUnconvertible, SubjectId, "fp-1", new TestClock()));
        var badgeCache = new FakeTidyUpBadgeCache();
        var command = new RestoreFindingCommand(dismissals, badgeCache, NullLogger<RestoreFindingCommand>.Instance);

        await command.ExecuteAsync(Household, DetectorId.StockUnitUnconvertible, SubjectId);

        Assert.Equal(1, badgeCache.InvalidateCallCount);
    }
}
