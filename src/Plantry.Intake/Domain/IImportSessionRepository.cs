using Plantry.SharedKernel;

namespace Plantry.Intake.Domain;

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
}
