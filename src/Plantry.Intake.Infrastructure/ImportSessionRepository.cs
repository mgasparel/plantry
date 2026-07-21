using Microsoft.EntityFrameworkCore;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;

namespace Plantry.Intake.Infrastructure;

public sealed class ImportSessionRepository(IntakeDbContext db) : IImportSessionRepository
{
    public async Task AddAsync(ImportSession session, CancellationToken ct = default) =>
        await db.ImportSessions.AddAsync(session, ct);

    public async Task AddReceiptAsync(ImportReceipt receipt, CancellationToken ct = default) =>
        await db.ImportReceipts.AddAsync(receipt, ct);

    public Task<ImportSession?> FindAsync(ImportSessionId sessionId, CancellationToken ct = default) =>
        db.ImportSessions
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);

    // Receipt is kept off the hot path — load only on demand.
    public Task<ImportReceipt?> FindReceiptAsync(ImportSessionId sessionId, CancellationToken ct = default) =>
        db.ImportReceipts.FirstOrDefaultAsync(r => r.Id == sessionId, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);

    public Task<List<ImportSession>> ListPendingAsync(HouseholdId householdId, CancellationToken ct = default) =>
        db.ImportSessions
            .Include(s => s.Lines)
            .Where(s => s.HouseholdId == householdId && s.Status == ImportStatus.Ready)
            .ToListAsync(ct);

    public Task<bool> HasPendingAsync(HouseholdId householdId, CancellationToken ct = default) =>
        db.ImportSessions
            .AnyAsync(s => s.HouseholdId == householdId && s.Status == ImportStatus.Ready, ct);

    public Task<List<ImportSession>> ListRecentAsync(HouseholdId householdId, int take = 10, CancellationToken ct = default) =>
        db.ImportSessions
            .Include(s => s.Lines)
            .Where(s => s.HouseholdId == householdId &&
                        (s.Status == ImportStatus.Ready ||
                         s.Status == ImportStatus.Committed ||
                         s.Status == ImportStatus.Failed))
            .OrderByDescending(s => s.CreatedAt)
            .Take(take)
            .ToListAsync(ct);

    // No .Include(Lines): the monthly stats read only session-level columns (Status, CreatedAt,
    // CommittedAt, ParsedAt, Total), so keep the lines off the query. The CreatedAt-OR-CommittedAt
    // union window is applied in SQL; status/null semantics are applied by the query.
    public Task<List<ImportSession>> ListInMonthWindowAsync(
        HouseholdId householdId,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken ct = default)
    {
        // Normalize the window bounds to UTC before they become SQL parameters. Npgsql rejects a
        // DateTimeOffset with a non-UTC offset when writing to 'timestamp with time zone' (it throws
        // "only offset 0 (UTC) is supported"). Callers compute the month window in server-local time
        // (GetMonthlyIntakeStatsQuery uses clock.UtcNow.ToLocalTime()), so the offset is non-zero off
        // UTC machines. ToUniversalTime() preserves the exact instant, so the comparison is unchanged.
        var startUtc = windowStart.ToUniversalTime();
        var endUtc = windowEnd.ToUniversalTime();
        return db.ImportSessions
            .Where(s => s.HouseholdId == householdId &&
                        ((s.CreatedAt >= startUtc && s.CreatedAt <= endUtc) ||
                         (s.CommittedAt != null &&
                          s.CommittedAt >= startUtc && s.CommittedAt <= endUtc)))
            .ToListAsync(ct);
    }

    // Every status but Parsing (a live upload, never a completed history entry) — includes Discarded,
    // which ListRecentAsync deliberately excludes (receipt-intake-history.md H5: the history is a truthful
    // log of every scan, so a discarded session stays visible, just muted/unlinked at render time).
    public Task<List<ImportSession>> ListHistoryPageAsync(
        HouseholdId householdId, DateTimeOffset? beforeCreatedAt, int take, CancellationToken ct = default) =>
        db.ImportSessions
            .Include(s => s.Lines)
            .Where(s => s.HouseholdId == householdId &&
                        s.Status != ImportStatus.Parsing &&
                        (beforeCreatedAt == null || s.CreatedAt < beforeCreatedAt))
            .OrderByDescending(s => s.CreatedAt)
            .Take(take)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ImportLineProvenanceRow>> FindLinesForProvenanceAsync(
        HouseholdId householdId,
        IReadOnlyCollection<Guid> lineIds,
        IReadOnlyCollection<Guid> legacyJournalIds,
        CancellationToken ct = default)
    {
        if (lineIds.Count == 0 && legacyJournalIds.Count == 0)
            return [];

        // EF translates Contains against a value-converted id column as a SQL IN (...) clause (mirrors
        // RecipeRepository.GetRecipeNamesByIdAsync).
        var wantedLineIds = lineIds.Select(ImportLineId.From).ToHashSet();
        var wantedJournalIds = legacyJournalIds.ToHashSet();

        var lines = await db.ImportLines
            .Where(l => l.HouseholdId == householdId &&
                        (wantedLineIds.Contains(l.Id) ||
                         (l.JournalId != null && wantedJournalIds.Contains(l.JournalId.Value))))
            .Select(l => new { l.Id, l.SessionId, l.JournalId })
            .ToListAsync(ct);

        if (lines.Count == 0)
            return [];

        var sessionIds = lines.Select(l => l.SessionId).Distinct().ToList();
        var sessionsById = await db.ImportSessions
            .Where(s => sessionIds.Contains(s.Id))
            .Select(s => new { s.Id, s.MerchantText, s.PurchaseDate, s.CreatedAt })
            .ToDictionaryAsync(s => s.Id, ct);

        return lines
            // Defensive: a line matched via the new-style lineIds branch is expected to already carry its
            // own committed JournalId, but a session removed underneath us or a not-actually-committed line
            // must not NRE — both are simply unresolvable (fall back to plain source text at the page).
            .Where(l => l.JournalId is not null && sessionsById.ContainsKey(l.SessionId))
            .Select(l =>
            {
                var session = sessionsById[l.SessionId];
                return new ImportLineProvenanceRow(
                    l.Id.Value, l.SessionId.Value, l.JournalId!.Value,
                    session.MerchantText, session.PurchaseDate, session.CreatedAt);
            })
            .ToList();
    }
}
