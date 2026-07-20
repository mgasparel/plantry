using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Intake.Application;

/// <summary>
/// Aggregate stats for the "This month" card on the Add groceries page, scoped to the current
/// calendar month (server-local time) for one household.
/// </summary>
/// <param name="ReceiptsScanned">
/// Count of sessions created this month in any status except Discarded — a failed parse still
/// counts as a scan the user performed.
/// </param>
/// <param name="GroceriesTotal">
/// Sum of the receipt <c>Total</c> across sessions committed this month; sessions with a null
/// Total contribute 0. Zero when nothing was committed this month.
/// </param>
/// <param name="AverageReviewTime">
/// Mean of (CommittedAt − ParsedAt) across sessions committed this month that carry both
/// timestamps; null when there are none.
/// </param>
public sealed record MonthlyIntakeStats(
    int ReceiptsScanned,
    decimal GroceriesTotal,
    TimeSpan? AverageReviewTime);

/// <summary>
/// Computes the current-month intake stats for a household. The repository supplies a lean window
/// list (sessions whose <see cref="ImportSession.CreatedAt"/> OR
/// <see cref="ImportSession.CommittedAt"/> falls in the month window); this query applies the
/// status/null semantics so they stay unit-testable. The window spans the start of the current
/// calendar month to now, computed in server-local time to match how intake dates are displayed.
/// </summary>
public sealed class GetMonthlyIntakeStatsQuery(IImportSessionRepository sessions, IClock clock)
{
    public async Task<MonthlyIntakeStats> ExecuteAsync(
        HouseholdId householdId,
        CancellationToken ct = default)
    {
        var now = clock.UtcNow.ToLocalTime();
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);

        var list = await sessions.ListInMonthWindowAsync(householdId, monthStart, now, ct);

        // Scanned: created in the window, any status but Discarded. A session committed this month
        // but created earlier is in the list (for the totals below) yet must not count as a scan.
        var receiptsScanned = list.Count(s =>
            s.Status != ImportStatus.Discarded &&
            s.CreatedAt >= monthStart && s.CreatedAt <= now);

        // Committed in the window drives both the money total and the review-time average.
        var committedInWindow = list
            .Where(s => s.CommittedAt is { } committedAt &&
                        committedAt >= monthStart && committedAt <= now)
            .ToList();

        var groceriesTotal = committedInWindow.Sum(s => s.Total ?? 0m);

        var reviewTimes = committedInWindow
            .Where(s => s.ParsedAt is not null && s.CommittedAt is not null)
            .Select(s => s.CommittedAt!.Value - s.ParsedAt!.Value)
            .ToList();

        TimeSpan? averageReviewTime = reviewTimes.Count > 0
            ? TimeSpan.FromTicks(reviewTimes.Sum(t => t.Ticks) / reviewTimes.Count)
            : null;

        return new MonthlyIntakeStats(receiptsScanned, groceriesTotal, averageReviewTime);
    }
}
