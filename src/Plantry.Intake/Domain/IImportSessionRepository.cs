using Plantry.SharedKernel;

namespace Plantry.Intake.Domain;

/// <summary>
/// Lean projection of one <see cref="ImportLine"/> for provenance-chip resolution (receipt-intake-history.md
/// H2/H4) — just enough to build the chip label/href without loading the full session/line aggregates.
/// </summary>
/// <param name="ImportLineId">The line's own id — the new-style (post-H1) correlation key and the deep-link anchor.</param>
/// <param name="SessionId">The owning session's id — the chip's link target.</param>
/// <param name="JournalId">The line's own <c>JournalId</c> column (the value stamped by <c>MarkCommitted</c>) — the
/// legacy (pre-H1) correlation key: a journal row with a null <c>SourceRef</c> is matched by this value instead.</param>
/// <param name="MerchantText">The owning session's merchant name, or null (renders as "Unknown store").</param>
/// <param name="PurchaseDate">The owning session's (possibly user-corrected) purchase date, or null.</param>
/// <param name="SessionCreatedAt">The owning session's creation time — the display-date fallback when <see cref="PurchaseDate"/> is null.</param>
public sealed record ImportLineProvenanceRow(
    Guid ImportLineId,
    Guid SessionId,
    Guid JournalId,
    string? MerchantText,
    DateOnly? PurchaseDate,
    DateTimeOffset SessionCreatedAt);

public interface IImportSessionRepository
{
    Task AddAsync(ImportSession session, CancellationToken ct = default);
    Task AddReceiptAsync(ImportReceipt receipt, CancellationToken ct = default);
    Task<ImportSession?> FindAsync(ImportSessionId sessionId, CancellationToken ct = default);
    Task<ImportReceipt?> FindReceiptAsync(ImportSessionId sessionId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
    Task<List<ImportSession>> ListPendingAsync(HouseholdId householdId, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the household has at least one pending (Ready) import session — used for
    /// the Today-page cold-start check to avoid materializing the full pending list.
    /// </summary>
    Task<bool> HasPendingAsync(HouseholdId householdId, CancellationToken ct = default);

    /// <summary>
    /// Returns the most recent <paramref name="take"/> sessions for the household across all
    /// terminal and in-progress statuses (Ready, Committed, Failed), ordered newest first.
    /// Excludes Parsing and Discarded sessions.
    /// </summary>
    Task<List<ImportSession>> ListRecentAsync(HouseholdId householdId, int take = 10, CancellationToken ct = default);

    /// <summary>
    /// Returns every session for the household whose <see cref="ImportSession.CreatedAt"/> OR
    /// <see cref="ImportSession.CommittedAt"/> falls within the inclusive window
    /// [<paramref name="windowStart"/>, <paramref name="windowEnd"/>]. The union of the two date
    /// columns is deliberate: a session created before the window but committed inside it still
    /// contributes to committed-based aggregates, while a session created inside the window counts
    /// as a scan even if never committed. No status filtering is applied here — callers decide which
    /// statuses to include so the semantics stay testable outside the database.
    /// </summary>
    Task<List<ImportSession>> ListInMonthWindowAsync(
        HouseholdId householdId,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken ct = default);

    /// <summary>
    /// Returns one page of history rows (receipt-intake-history.md H5/H6) for the household — every status
    /// except <see cref="ImportStatus.Parsing"/> (a live upload has no place in a completed history),
    /// newest-scan-first (<see cref="ImportSession.CreatedAt"/> descending), with <see cref="ImportSession.Lines"/>
    /// eagerly loaded so the query layer can project item counts/totals. When <paramref name="beforeCreatedAt"/>
    /// is supplied, only sessions scanned strictly before that instant are returned (the "Show earlier"
    /// cursor). Paging is keyed on the indexed <c>created_at</c> column rather than the displayed
    /// purchase-date label — in the overwhelming common case (a receipt scanned the day of the purchase)
    /// the two coincide; a receipt scanned well after the fact may display under a slightly different
    /// month header than its scan-order position in the page. A page shorter than <paramref name="take"/>
    /// means there is nothing earlier.
    /// </summary>
    Task<List<ImportSession>> ListHistoryPageAsync(
        HouseholdId householdId,
        DateTimeOffset? beforeCreatedAt,
        int take,
        CancellationToken ct = default);

    /// <summary>
    /// Batch-resolves <see cref="ImportLineProvenanceRow"/> projections for provenance-chip resolution
    /// (receipt-intake-history.md H2/H4): lines whose own id is in <paramref name="lineIds"/> (the new-style,
    /// post-H1 correlation — a journal row's <c>SourceRef</c> IS the line's id) unioned with lines whose own
    /// <c>JournalId</c> column is in <paramref name="legacyJournalIds"/> (the H2 legacy fallback for a
    /// pre-H1 row whose journal <c>SourceRef</c> is null — reverse-resolved off the line's own committed
    /// <c>JournalId</c>). Household-scoped; either list may be empty.
    /// </summary>
    Task<IReadOnlyList<ImportLineProvenanceRow>> FindLinesForProvenanceAsync(
        HouseholdId householdId,
        IReadOnlyCollection<Guid> lineIds,
        IReadOnlyCollection<Guid> legacyJournalIds,
        CancellationToken ct = default);
}
