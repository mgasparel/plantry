using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Intake.Application;

/// <summary>One row of the browsable intake history (receipt-intake-history.md H5/H6).</summary>
/// <param name="Date">
/// <see cref="ImportSession.PurchaseDate"/> when the receipt parsed one, else the session's scan date —
/// a receipt scanned days after the actual shopping trip displays under its purchase month, not its
/// scan month, when the receipt carries that date.
/// </param>
/// <param name="ItemCount">See <see cref="IntakeSessionProjection.ItemCount"/>; null renders as "—".</param>
/// <param name="Total">See <see cref="IntakeSessionProjection.Amount"/>; null renders as "—".</param>
public sealed record IntakeHistoryRow(
    ImportSessionId Id,
    string? Store,
    DateOnly Date,
    ImportStatus Status,
    int? ItemCount,
    decimal? Total);

/// <summary>One page of history rows plus the cursor for the next "Show earlier" fetch.</summary>
/// <param name="NextCursor">
/// Null when this page was shorter than the requested size (nothing earlier exists); otherwise the
/// <c>CreatedAt</c> to pass as <see cref="GetIntakeHistoryQuery.ExecuteAsync"/>'s <c>beforeCreatedAt</c>
/// for the next page.
/// </param>
public sealed record IntakeHistoryPage(IReadOnlyList<IntakeHistoryRow> Rows, DateTimeOffset? NextCursor);

/// <summary>
/// Read query for <c>/Intake/History</c> (receipt-intake-history.md H5): a paged, newest-scan-first log of
/// every receipt intake regardless of status — Committed, Ready ("being reviewed"), Failed, and Discarded
/// all appear, so the page is a truthful record of every scan rather than only the successful ones.
/// </summary>
public sealed class GetIntakeHistoryQuery(IImportSessionRepository sessions)
{
    /// <summary>Default page size — large enough to typically span more than one calendar month per fetch.</summary>
    public const int DefaultPageSize = 20;

    public async Task<IntakeHistoryPage> ExecuteAsync(
        HouseholdId householdId,
        DateTimeOffset? beforeCreatedAt = null,
        int take = DefaultPageSize,
        CancellationToken ct = default)
    {
        var page = await sessions.ListHistoryPageAsync(householdId, beforeCreatedAt, take, ct);

        var rows = page.Select(ToRow).ToList();
        var nextCursor = page.Count == take ? page[^1].CreatedAt : (DateTimeOffset?)null;

        return new IntakeHistoryPage(rows, nextCursor);
    }

    private static IntakeHistoryRow ToRow(ImportSession s)
    {
        var date = s.PurchaseDate ?? DateOnly.FromDateTime(s.CreatedAt.LocalDateTime);
        return new IntakeHistoryRow(
            s.Id, s.MerchantText, date, s.Status,
            IntakeSessionProjection.ItemCount(s), IntakeSessionProjection.Amount(s));
    }
}
