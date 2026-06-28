using Plantry.Intake.Domain;
using Plantry.SharedKernel;

namespace Plantry.Intake.Application;

/// <summary>
/// Projection of a single pending-review intake session for the Today review-banner stack.
/// </summary>
public sealed record PendingReviewRow(
    ImportSessionId Id,
    string? Store,
    DateTimeOffset CreatedAt,
    int ItemCount,
    decimal? Amount);

/// <summary>
/// Returns the intake sessions that are in <see cref="ImportStatus.Ready"/> status for a
/// household, projected to lightweight <see cref="PendingReviewRow"/> view models suitable
/// for the Today page review-banner stack (SPEC Page 0 §0b, plantry-yb6).
///
/// Only <c>Ready</c> sessions are returned — sessions that have been parsed by AI and are
/// waiting for the user to review and commit (or discard) them. Committed, Failed, and
/// Discarded sessions are excluded.
///
/// Data: <see cref="IImportSessionRepository.ListPendingAsync"/> already scopes to Ready +
/// household. Amount is derived by summing <c>SuggestedPrice</c> across all lines (null when
/// none of the lines carry a price — the parser did not extract amounts).
/// </summary>
public sealed class PendingReviewQuery(IImportSessionRepository sessions)
{
    public async Task<IReadOnlyList<PendingReviewRow>> ExecuteAsync(
        HouseholdId householdId,
        CancellationToken ct = default)
    {
        var list = await sessions.ListPendingAsync(householdId, ct);

        return list
            .OrderByDescending(s => s.CreatedAt)
            .Select(s =>
            {
                var prices = s.Lines
                    .Where(l => l.SuggestedPrice.HasValue)
                    .Select(l => l.SuggestedPrice!.Value)
                    .ToList();

                decimal? amount = prices.Count > 0 ? prices.Sum() : null;

                return new PendingReviewRow(
                    s.Id,
                    s.MerchantText,
                    s.CreatedAt,
                    s.Lines.Count,
                    amount);
            })
            .ToList();
    }
}
