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
/// Returns the most recent intake sessions for a household, projected into view models.
/// Amount is derived by summing SuggestedPrice across all lines (falls back to null when none
/// of the lines carry a price).
/// </summary>
public sealed class GetRecentSessionsQuery(IImportSessionRepository sessions)
{
    public async Task<IReadOnlyList<RecentIntakeRow>> ExecuteAsync(
        HouseholdId householdId,
        int take = 10,
        CancellationToken ct = default)
    {
        var list = await sessions.ListRecentAsync(householdId, take, ct);

        return list.Select(s =>
        {
            var prices = s.Lines
                .Where(l => l.SuggestedPrice.HasValue)
                .Select(l => l.SuggestedPrice!.Value)
                .ToList();

            decimal? amount = prices.Count > 0 ? prices.Sum() : null;

            return new RecentIntakeRow(s.Id, s.MerchantText, s.CreatedAt, s.Status, amount);
        }).ToList();
    }
}
