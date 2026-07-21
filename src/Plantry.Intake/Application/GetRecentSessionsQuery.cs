using Plantry.Intake.Domain;
using Plantry.SharedKernel;

namespace Plantry.Intake.Application;

/// <summary>View model for a single row in the recent-intakes history list.</summary>
public sealed record RecentIntakeRow(
    ImportSessionId Id,
    string? Store,
    DateTimeOffset CreatedAt,
    ImportStatus Status,
    decimal? Amount);

/// <summary>
/// Returns the most recent intake sessions for a household, projected into view models. Amount follows
/// the shared H6 rule (<see cref="IntakeSessionProjection.Amount"/>): a committed session prefers the
/// receipt's parsed total, falling back to the sum of committed line prices; a Ready session sums the
/// AI-suggested prices instead — it no longer reports a suggested-price sum for an already-committed
/// session, which would ignore any price the user corrected during review.
/// </summary>
public sealed class GetRecentSessionsQuery(IImportSessionRepository sessions)
{
    public async Task<IReadOnlyList<RecentIntakeRow>> ExecuteAsync(
        HouseholdId householdId,
        int take = 10,
        CancellationToken ct = default)
    {
        var list = await sessions.ListRecentAsync(householdId, take, ct);

        return list
            .Select(s => new RecentIntakeRow(s.Id, s.MerchantText, s.CreatedAt, s.Status, IntakeSessionProjection.Amount(s)))
            .ToList();
    }
}
