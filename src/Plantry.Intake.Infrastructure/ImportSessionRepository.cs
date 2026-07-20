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
}
