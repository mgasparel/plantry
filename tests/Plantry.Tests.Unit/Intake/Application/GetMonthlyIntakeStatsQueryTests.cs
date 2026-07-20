using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Intake.Application;

/// <summary>
/// L2 tests (fake repository, no DB) for <see cref="GetMonthlyIntakeStatsQuery"/>. The clock is
/// pinned mid-month (2026-03-15) so the current-month window is [2026-03-01, now]; every timestamp
/// is placed well clear of a day/month boundary so <c>ToLocalTime</c> cannot shift it across the
/// window edge on any CI timezone. Covers each acceptance criterion: empty month, boundary
/// straddling, Discarded excluded from scans, Failed counted but not totalled, null Total, and a
/// committed session whose ParsedAt is missing being skipped in the average.
/// </summary>
public sealed class GetMonthlyIntakeStatsQueryTests
{
    // Pinned "now": mid-month, noon UTC — robust to any server offset.
    private static readonly DateTimeOffset Now = new(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);
    // Well inside the current month.
    private static readonly DateTimeOffset InMonth = new(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);
    // The previous calendar month.
    private static readonly DateTimeOffset PrevMonth = new(2026, 2, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _householdId = Guid.NewGuid();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly MutableClock _clock = new(Now);

    // ── Builders ──────────────────────────────────────────────────────────────

    private ImportSession StartAt(DateTimeOffset createdAt)
    {
        _clock.Set(createdAt);
        return ImportSession.Start(HouseholdId.From(_householdId), ImportSourceType.Receipt, _userId, _clock);
    }

    private ImportSession Committed(
        DateTimeOffset createdAt, DateTimeOffset parsedAt, DateTimeOffset committedAt, decimal? total)
    {
        var s = StartAt(createdAt);
        s.AddLine(1, "ITEM", SuggestedConfidence.High, rawPayload: null);
        s.MarkReady("Store", parsedAt, total is null ? null : new ReceiptMetadata(Total: total));
        s.MarkCommitted(committedAt);
        return s;
    }

    private ImportSession Ready(DateTimeOffset createdAt, DateTimeOffset parsedAt)
    {
        var s = StartAt(createdAt);
        s.MarkReady("Store", parsedAt);
        return s;
    }

    private ImportSession Failed(DateTimeOffset createdAt)
    {
        var s = StartAt(createdAt);
        s.MarkParsingFailed("unreadable");
        return s;
    }

    private ImportSession Discarded(DateTimeOffset createdAt)
    {
        var s = StartAt(createdAt);
        s.Discard();
        return s;
    }

    private FakeImportSessionRepository RepoWith(params ImportSession[] sessions)
    {
        var repo = new FakeImportSessionRepository();
        repo.Sessions.AddRange(sessions);
        return repo;
    }

    private async Task<MonthlyIntakeStats> ExecuteAsync(FakeImportSessionRepository repo)
    {
        _clock.Set(Now);
        return await new GetMonthlyIntakeStatsQuery(repo, _clock)
            .ExecuteAsync(HouseholdId.From(_householdId));
    }

    // ── Empty month ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Empty_month_yields_zero_scans_zero_total_null_average()
    {
        var stats = await ExecuteAsync(new FakeImportSessionRepository());

        Assert.Equal(0, stats.ReceiptsScanned);
        Assert.Equal(0m, stats.GroceriesTotal);
        Assert.Null(stats.AverageReviewTime);
    }

    // ── ReceiptsScanned ───────────────────────────────────────────────────────

    [Fact]
    public async Task Scans_count_sessions_created_this_month()
    {
        var repo = RepoWith(
            Committed(InMonth, InMonth.AddMinutes(30), InMonth.AddMinutes(60), total: 10m),
            Ready(InMonth, InMonth.AddMinutes(5)));

        var stats = await ExecuteAsync(repo);

        Assert.Equal(2, stats.ReceiptsScanned);
    }

    [Fact]
    public async Task Session_created_last_month_but_committed_this_month_is_not_a_scan()
    {
        // Straddles the boundary: created in February, committed in March.
        var repo = RepoWith(
            Committed(PrevMonth, PrevMonth.AddMinutes(30), InMonth, total: 25m));

        var stats = await ExecuteAsync(repo);

        // Not scanned this month (created last month) …
        Assert.Equal(0, stats.ReceiptsScanned);
        // … but its committed value still lands in this month's total.
        Assert.Equal(25m, stats.GroceriesTotal);
    }

    [Fact]
    public async Task Discarded_session_is_excluded_from_scans()
    {
        var repo = RepoWith(
            Discarded(InMonth),
            Ready(InMonth, InMonth.AddMinutes(5)));

        var stats = await ExecuteAsync(repo);

        // Only the Ready session counts; the Discarded one does not.
        Assert.Equal(1, stats.ReceiptsScanned);
    }

    [Fact]
    public async Task Failed_session_counts_as_scan_but_not_toward_total()
    {
        var repo = RepoWith(Failed(InMonth));

        var stats = await ExecuteAsync(repo);

        Assert.Equal(1, stats.ReceiptsScanned);
        Assert.Equal(0m, stats.GroceriesTotal);
        Assert.Null(stats.AverageReviewTime);
    }

    // ── GroceriesTotal ────────────────────────────────────────────────────────

    [Fact]
    public async Task Total_sums_committed_session_totals_in_the_window()
    {
        var repo = RepoWith(
            Committed(InMonth, InMonth.AddMinutes(10), InMonth.AddMinutes(20), total: 12.50m),
            Committed(InMonth, InMonth.AddMinutes(10), InMonth.AddMinutes(20), total: 7.25m));

        var stats = await ExecuteAsync(repo);

        Assert.Equal(19.75m, stats.GroceriesTotal);
    }

    [Fact]
    public async Task Committed_session_with_null_total_contributes_zero()
    {
        var repo = RepoWith(
            Committed(InMonth, InMonth.AddMinutes(10), InMonth.AddMinutes(20), total: null),
            Committed(InMonth, InMonth.AddMinutes(10), InMonth.AddMinutes(20), total: 5m));

        var stats = await ExecuteAsync(repo);

        Assert.Equal(5m, stats.GroceriesTotal);
    }

    [Fact]
    public async Task Ready_session_is_not_counted_in_total()
    {
        var repo = RepoWith(Ready(InMonth, InMonth.AddMinutes(5)));

        var stats = await ExecuteAsync(repo);

        Assert.Equal(0m, stats.GroceriesTotal);
    }

    // ── AverageReviewTime ─────────────────────────────────────────────────────

    [Fact]
    public async Task Average_review_time_is_mean_of_committed_review_durations()
    {
        var repo = RepoWith(
            // 30-minute review …
            Committed(InMonth, InMonth, InMonth.AddMinutes(30), total: 1m),
            // … and a 90-minute review → mean 60 minutes.
            Committed(InMonth, InMonth, InMonth.AddMinutes(90), total: 1m));

        var stats = await ExecuteAsync(repo);

        Assert.Equal(TimeSpan.FromMinutes(60), stats.AverageReviewTime);
    }

    [Fact]
    public async Task Ready_session_is_excluded_from_the_average()
    {
        var repo = RepoWith(
            Committed(InMonth, InMonth, InMonth.AddMinutes(40), total: 1m),
            // Ready has a ParsedAt but no CommittedAt — must not skew the average.
            Ready(InMonth, InMonth.AddMinutes(5)));

        var stats = await ExecuteAsync(repo);

        Assert.Equal(TimeSpan.FromMinutes(40), stats.AverageReviewTime);
    }

    [Fact]
    public async Task Committed_session_with_null_parsed_at_is_skipped_in_the_average()
    {
        // The domain never produces a committed session with a null ParsedAt (MarkReady always
        // stamps it), so force the state via reflection to prove the query's defensive guard.
        var withoutParsedAt = Committed(InMonth, InMonth, InMonth.AddMinutes(20), total: 1m);
        ClearParsedAt(withoutParsedAt);
        var normal = Committed(InMonth, InMonth, InMonth.AddMinutes(50), total: 1m);

        var repo = RepoWith(withoutParsedAt, normal);

        var stats = await ExecuteAsync(repo);

        // Only the normal 50-minute review contributes; the null-ParsedAt one is skipped.
        Assert.Equal(TimeSpan.FromMinutes(50), stats.AverageReviewTime);
        // It is still counted as a committed scan and its total still lands.
        Assert.Equal(2, stats.ReceiptsScanned);
        Assert.Equal(2m, stats.GroceriesTotal);
    }

    [Fact]
    public async Task Average_is_null_when_no_committed_sessions_this_month()
    {
        var repo = RepoWith(Ready(InMonth, InMonth.AddMinutes(5)), Failed(InMonth));

        var stats = await ExecuteAsync(repo);

        Assert.Null(stats.AverageReviewTime);
    }

    // ── Tenancy ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Other_household_sessions_are_not_counted()
    {
        var otherHousehold = ImportSession.Start(
            HouseholdId.New(), ImportSourceType.Receipt, _userId, new MutableClock(InMonth));
        otherHousehold.AddLine(1, "ITEM", SuggestedConfidence.High, rawPayload: null);
        otherHousehold.MarkReady("Store", InMonth.AddMinutes(10), new ReceiptMetadata(Total: 99m));
        otherHousehold.MarkCommitted(InMonth.AddMinutes(20));

        var repo = RepoWith(
            Committed(InMonth, InMonth.AddMinutes(10), InMonth.AddMinutes(20), total: 4m),
            otherHousehold);

        var stats = await ExecuteAsync(repo);

        Assert.Equal(1, stats.ReceiptsScanned);
        Assert.Equal(4m, stats.GroceriesTotal);
    }

    private static void ClearParsedAt(ImportSession session) =>
        typeof(ImportSession).GetProperty(nameof(ImportSession.ParsedAt))!
            .SetValue(session, null);
}

/// <summary>A settable clock for placing session timestamps relative to a pinned "now".</summary>
internal sealed class MutableClock(DateTimeOffset now) : IClock
{
    private DateTimeOffset _now = now;
    public DateTimeOffset UtcNow => _now;
    public void Set(DateTimeOffset now) => _now = now;
}
