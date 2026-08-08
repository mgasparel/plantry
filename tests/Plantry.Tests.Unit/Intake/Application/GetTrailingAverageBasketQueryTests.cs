using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Intake.Application;

/// <summary>
/// L2 tests (fake repository, no DB) for <see cref="GetTrailingAverageBasketQuery"/> — the Intake-review
/// trip-context stat (plantry-bb7p, stats-page-prototype.html injection appendix). Exercises the
/// repository's default <c>ListRecentCommittedTotalsAsync</c> implementation via <see cref="FakeImportSessionRepository"/>
/// (which only overrides <c>ListRecentAsync</c>), so these tests also pin that default's Committed-only /
/// null-Total-excluded filtering.
/// </summary>
public sealed class GetTrailingAverageBasketQueryTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _householdId = Guid.NewGuid();
    private readonly Guid _userId = Guid.CreateVersion7();

    private ImportSession CommittedSession(decimal? total, DateTimeOffset committedAt)
    {
        var session = ImportSession.Start(
            HouseholdId.From(_householdId), ImportSourceType.Receipt, _userId, Clock);
        session.MarkReady("Test Grocer", Clock.UtcNow, new ReceiptMetadata(Total: total));
        session.MarkCommitted(committedAt);
        return session;
    }

    [Fact(DisplayName = "Null when the household has no committed sessions")]
    public async Task Null_When_No_Committed_Sessions()
    {
        var repo = new FakeImportSessionRepository();
        var query = new GetTrailingAverageBasketQuery(repo);

        var result = await query.ExecuteAsync(HouseholdId.From(_householdId));

        Assert.Null(result);
    }

    [Fact(DisplayName = "Averages the committed totals, rounded to 2 decimal places")]
    public async Task Averages_Committed_Totals()
    {
        var repo = new FakeImportSessionRepository();
        var now = DateTimeOffset.UtcNow;
        repo.Sessions.Add(CommittedSession(80.00m, now.AddDays(-1)));
        repo.Sessions.Add(CommittedSession(92.50m, now.AddDays(-8)));
        repo.Sessions.Add(CommittedSession(86.00m, now.AddDays(-15)));
        var query = new GetTrailingAverageBasketQuery(repo);

        var result = await query.ExecuteAsync(HouseholdId.From(_householdId));

        Assert.Equal(86.17m, result); // (80.00 + 92.50 + 86.00) / 3 = 86.1666... -> 86.17
    }

    [Fact(DisplayName = "A committed session with a null Total is excluded rather than treated as zero")]
    public async Task Null_Total_Sessions_Are_Excluded()
    {
        var repo = new FakeImportSessionRepository();
        var now = DateTimeOffset.UtcNow;
        repo.Sessions.Add(CommittedSession(100.00m, now.AddDays(-1)));
        repo.Sessions.Add(CommittedSession(null, now.AddDays(-2)));
        var query = new GetTrailingAverageBasketQuery(repo);

        var result = await query.ExecuteAsync(HouseholdId.From(_householdId));

        Assert.Equal(100.00m, result);
    }

    [Fact(DisplayName = "A still-Ready (not yet committed) session never contributes to the average")]
    public async Task Ready_Sessions_Are_Excluded()
    {
        var repo = new FakeImportSessionRepository();
        var session = ImportSession.Start(HouseholdId.From(_householdId), ImportSourceType.Receipt, _userId, Clock);
        session.MarkReady("Test Grocer", Clock.UtcNow, new ReceiptMetadata(Total: 55.00m));
        repo.Sessions.Add(session); // Ready, not Committed

        var query = new GetTrailingAverageBasketQuery(repo);
        var result = await query.ExecuteAsync(HouseholdId.From(_householdId));

        Assert.Null(result);
    }

    [Fact(DisplayName = "Only the WindowSize most-recently-committed sessions are averaged")]
    public async Task Caps_At_WindowSize_Newest_Committed_Sessions()
    {
        var repo = new FakeImportSessionRepository();
        var now = DateTimeOffset.UtcNow;
        // WindowSize (10) sessions at 100, plus one older outlier at 0 that must be excluded.
        for (var i = 0; i < GetTrailingAverageBasketQuery.WindowSize; i++)
            repo.Sessions.Add(CommittedSession(100.00m, now.AddDays(-i)));
        repo.Sessions.Add(CommittedSession(0.00m, now.AddDays(-100)));

        var query = new GetTrailingAverageBasketQuery(repo);
        var result = await query.ExecuteAsync(HouseholdId.From(_householdId));

        Assert.Equal(100.00m, result);
    }
}
