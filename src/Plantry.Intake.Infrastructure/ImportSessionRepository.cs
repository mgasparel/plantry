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
}
